using Pulaski.Contracts.Processes;
using Pulaski.Web.Services.Scheduling;

namespace Pulaski.Web.Services.Processes;

/// <summary>
/// The edits the process editor makes, as pure functions from one definition to the next. The
/// editor saves the result after each one; the API checks it again (D-023).
/// </summary>
public static class ProcessEditing
{
    public const string AfterStart = "after_start";
    public const string AfterEnd = "after_end";

    public const double NodeWidth = 232;
    public const double ColumnGap = 96;
    public const double RowGap = 40;
    public const double Margin = 32;

    /// <summary>Drawn height of an operation box: header, one row per equipment entity, and the drop hint.</summary>
    public static double NodeHeight(ProcessOperationDto op) => 52 + Math.Max(1, op.Equipment.Count) * 40 + 12;

    /// <summary>The next operation number: the next multiple of ten above the highest.</summary>
    public static int NextNumber(ProcessDefinitionDto definition) =>
        definition.Operations.Count == 0 ? 10 : (definition.Operations.Max(o => o.Number) / 10 + 1) * 10;

    public static (ProcessDefinitionDto Definition, Guid OperationId) AddOperation(
        ProcessDefinitionDto definition, Guid id, string? name = null, double? x = null, double? y = null, Guid? equipmentId = null)
    {
        var number = NextNumber(definition);
        var right = definition.Operations.Count == 0 ? Margin : definition.Operations.Max(o => o.X) + NodeWidth + ColumnGap;
        var op = new ProcessOperationDto
        {
            Id = id,
            Number = number,
            Name = name ?? $"Operation {number}",
            X = Snap(x ?? right),
            Y = Snap(y ?? Margin),
            Equipment = equipmentId is { } eq ? [new OperationEquipmentDto(eq, 100m)] : [],
        };
        return (definition with { Operations = [.. definition.Operations, op] }, id);
    }

    public static ProcessDefinitionDto UpdateOperation(ProcessDefinitionDto definition, Guid id, Func<ProcessOperationDto, ProcessOperationDto> change) =>
        definition with { Operations = [.. definition.Operations.Select(o => o.Id == id ? change(o) : o)] };

    public static ProcessDefinitionDto RemoveOperation(ProcessDefinitionDto definition, Guid id) => definition with
    {
        Operations = [.. definition.Operations.Where(o => o.Id != id)],
        Dependencies = [.. definition.Dependencies.Where(d => d.FromOperationId != id && d.ToOperationId != id)],
    };

    public static ProcessDefinitionDto Move(ProcessDefinitionDto definition, Guid id, double x, double y) =>
        UpdateOperation(definition, id, o => o with { X = Snap(Math.Max(0, x)), Y = Snap(Math.Max(0, y)) });

    /// <summary>Adds the equipment to the operation and splits the operation evenly again; no change if it is already there.</summary>
    public static ProcessDefinitionDto AddEquipment(ProcessDefinitionDto definition, Guid operationId, Guid equipmentId) =>
        UpdateOperation(definition, operationId, o => o.Equipment.Any(e => e.EquipmentId == equipmentId)
            ? o
            : SplitEvenly(o with { Equipment = [.. o.Equipment, new OperationEquipmentDto(equipmentId, 0m)] }));

    public static ProcessDefinitionDto RemoveEquipment(ProcessDefinitionDto definition, Guid operationId, Guid equipmentId) =>
        UpdateOperation(definition, operationId, o => SplitEvenly(o with { Equipment = [.. o.Equipment.Where(e => e.EquipmentId != equipmentId)] }));

    public static ProcessDefinitionDto SetShare(ProcessDefinitionDto definition, Guid operationId, Guid equipmentId, decimal share) =>
        UpdateOperation(definition, operationId, o => o with
        {
            Equipment = [.. o.Equipment.Select(e => e.EquipmentId == equipmentId ? e with { SharePercent = decimal.Round(share, 2) } : e)],
        });

    /// <summary>Even shares to two decimals; the last entity takes the rounding (33.33, 33.33, 33.34).</summary>
    public static ProcessOperationDto SplitEvenly(ProcessOperationDto op)
    {
        if (op.Equipment.Count == 0)
        {
            return op;
        }

        var each = decimal.Round(100m / op.Equipment.Count, 2, MidpointRounding.ToZero);
        var last = 100m - each * (op.Equipment.Count - 1);
        return op with { Equipment = [.. op.Equipment.Select((e, i) => e with { SharePercent = i == op.Equipment.Count - 1 ? last : each })] };
    }

    /// <summary>
    /// Links two operations, the second starting after the first starts (the usual flow-through).
    /// Null, with why, when the link exists already, joins an operation to itself, or would loop.
    /// </summary>
    public static (ProcessDefinitionDto? Definition, string? Refusal) Connect(ProcessDefinitionDto definition, Guid from, Guid to, Guid id)
    {
        if (from == to)
        {
            return (null, "An operation can't follow itself.");
        }

        if (definition.Dependencies.Any(d => d.FromOperationId == from && d.ToOperationId == to))
        {
            return (null, "Those operations are already linked.");
        }

        if (Reaches(definition, to, from))
        {
            return (null, "That link would loop back; a process must flow one way.");
        }

        return (definition with { Dependencies = [.. definition.Dependencies, new ProcessDependencyDto(id, from, to, AfterStart, TimeSpan.Zero)] }, null);
    }

    public static ProcessDefinitionDto UpdateDependency(ProcessDefinitionDto definition, Guid id, string type, TimeSpan lag) =>
        definition with { Dependencies = [.. definition.Dependencies.Select(d => d.Id == id ? d with { Type = type, Lag = lag } : d)] };

    public static ProcessDefinitionDto RemoveDependency(ProcessDefinitionDto definition, Guid id) =>
        definition with { Dependencies = [.. definition.Dependencies.Where(d => d.Id != id)] };

    /// <summary>
    /// Lays the operations out left to right in columns by how many operations come before them
    /// (the longest chain of links), ordered by number within a column.
    /// </summary>
    public static ProcessDefinitionDto AutoArrange(ProcessDefinitionDto definition)
    {
        var column = definition.Operations.ToDictionary(o => o.Id, _ => 0);
        for (var pass = 0; pass < definition.Operations.Count; pass++)
        {
            foreach (var link in definition.Dependencies.Where(d => column.ContainsKey(d.FromOperationId) && column.ContainsKey(d.ToOperationId)))
            {
                column[link.ToOperationId] = Math.Max(column[link.ToOperationId], column[link.FromOperationId] + 1);
            }
        }

        var placed = new Dictionary<Guid, (double X, double Y)>();
        foreach (var group in definition.Operations.GroupBy(o => column[o.Id]))
        {
            var y = Margin;
            foreach (var op in group.OrderBy(o => o.Number))
            {
                placed[op.Id] = (Margin + group.Key * (NodeWidth + ColumnGap), y);
                y += NodeHeight(op) + RowGap;
            }
        }

        return definition with { Operations = [.. definition.Operations.Select(o => o with { X = placed[o.Id].X, Y = placed[o.Id].Y })] };
    }

    /// <summary>The share total for an operation, for showing whether it adds up.</summary>
    public static decimal ShareTotal(ProcessOperationDto op) => op.Equipment.Sum(e => e.SharePercent);

    /// <summary>"After start" / "After end", with the lag when there is one.</summary>
    public static string Describe(ProcessDependencyDto link)
    {
        var type = link.Type == AfterEnd ? "after end" : "after start";
        return link.Lag > TimeSpan.Zero ? $"{type} +{ScheduleFormat.Duration(link.Lag)}" : type;
    }

    private static double Snap(double value) => Math.Round(value / 8) * 8;

    private static bool Reaches(ProcessDefinitionDto definition, Guid from, Guid target)
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>([from]);
        while (stack.TryPop(out var at))
        {
            if (at == target)
            {
                return true;
            }

            if (seen.Add(at))
            {
                foreach (var link in definition.Dependencies.Where(d => d.FromOperationId == at))
                {
                    stack.Push(link.ToOperationId);
                }
            }
        }

        return false;
    }
}
