namespace Pulaski.Domain.Processes;

/// <summary>How one operation follows another (ISA-95 segment dependency). Values match the stored definition.</summary>
public static class DependencyType
{
    /// <summary>Starts once the earlier operation has started: material flows straight through (extrude to pack).</summary>
    public const string AfterStart = "after_start";

    /// <summary>Starts once the earlier operation has ended: a hand-off of the finished quantity.</summary>
    public const string AfterEnd = "after_end";

    public static IReadOnlyList<string> All { get; } = [AfterStart, AfterEnd];
}

/// <summary>Process version lifecycle (D-023). Values match <c>core.process_version.status</c>.</summary>
public static class ProcessVersionStatus
{
    public const string Draft = "draft";
    public const string Released = "released";
    public const string Obsolete = "obsolete";
}

/// <summary>
/// Equipment that performs an operation, with its share of the operation's quantity. An operation
/// with several entities runs on all of them in parallel; each becomes its own job (D-023).
/// </summary>
public sealed record OperationEquipment(Guid EquipmentId, decimal SharePercent);

/// <summary>
/// An operation (ISA-95 operations segment) of a process: its number (10, 20, 30…), what it does,
/// and the equipment that does it. <c>X</c> and <c>Y</c> are only where the editor draws it.
/// </summary>
public sealed record ProcessOperation(
    Guid Id,
    int Number,
    string Name,
    string? Description,
    double X,
    double Y,
    IReadOnlyList<OperationEquipment> Equipment);

/// <summary>A link: <c>To</c> starts after <c>From</c> starts or ends (<see cref="DependencyType"/>), plus <c>Lag</c>.</summary>
public sealed record ProcessDependency(Guid Id, Guid FromOperationId, Guid ToOperationId, string Type, TimeSpan Lag);

/// <summary>Something wrong with a definition, pinned to the operation or link it concerns when there is one.</summary>
public sealed record ProcessProblem(string Code, string Message, Guid? OperationId = null, Guid? DependencyId = null);

/// <summary>
/// A process route (ISA-95 operations definition, D-023): operations and the links between them.
/// Operations without a path between them run in parallel; links make them sequential. A draft
/// may be incomplete but never malformed (<see cref="StructuralProblems"/>); a release must also
/// be complete (<see cref="ReleaseProblems"/>).
/// </summary>
public sealed record ProcessDefinition(IReadOnlyList<ProcessOperation> Operations, IReadOnlyList<ProcessDependency> Dependencies)
{
    public static ProcessDefinition Empty { get; } = new([], []);

    /// <summary>Problems that make the definition unusable even as a draft.</summary>
    public IReadOnlyList<ProcessProblem> StructuralProblems()
    {
        var problems = new List<ProcessProblem>();
        var ids = new HashSet<Guid>();

        foreach (var op in Operations)
        {
            if (!ids.Add(op.Id))
            {
                problems.Add(new("duplicate_operation", $"Operation {op.Number} appears twice.", op.Id));
            }

            if (op.Number <= 0)
            {
                problems.Add(new("operation_number", $"Operation numbers must be positive; {op.Number} is not.", op.Id));
            }

            if (string.IsNullOrWhiteSpace(op.Name) || op.Name.Length > 200)
            {
                problems.Add(new("operation_name", $"Operation {op.Number} needs a name of at most 200 characters.", op.Id));
            }

            foreach (var share in op.Equipment.Where(e => e.SharePercent is <= 0 or > 100))
            {
                problems.Add(new("share_range", $"Operation {op.Number}: each share must be more than 0% and at most 100%.", op.Id));
            }

            if (op.Equipment.GroupBy(e => e.EquipmentId).Any(g => g.Count() > 1))
            {
                problems.Add(new("duplicate_equipment", $"Operation {op.Number} lists the same equipment twice.", op.Id));
            }
        }

        foreach (var number in Operations.GroupBy(o => o.Number).Where(g => g.Count() > 1))
        {
            problems.Add(new("duplicate_number", $"Operation number {number.Key} is used {number.Count()} times.", number.First().Id));
        }

        var links = new HashSet<(Guid, Guid)>();
        foreach (var link in Dependencies)
        {
            if (!ids.Contains(link.FromOperationId) || !ids.Contains(link.ToOperationId))
            {
                problems.Add(new("unknown_operation", "A link points to an operation that isn't in the process.", DependencyId: link.Id));
            }
            else if (link.FromOperationId == link.ToOperationId)
            {
                problems.Add(new("self_link", "An operation can't follow itself.", link.FromOperationId, link.Id));
            }
            else if (!links.Add((link.FromOperationId, link.ToOperationId)))
            {
                problems.Add(new("duplicate_link", "Two operations are linked twice.", link.ToOperationId, link.Id));
            }

            if (!DependencyType.All.Contains(link.Type))
            {
                problems.Add(new("link_type", $"'{link.Type}' is not a link type.", DependencyId: link.Id));
            }

            if (link.Lag < TimeSpan.Zero)
            {
                problems.Add(new("link_lag", "A link's lag can't be negative.", DependencyId: link.Id));
            }
        }

        if (problems.Count == 0 && FindCycle() is { } cycle)
        {
            problems.Add(new("cycle", $"The links form a loop at or after operation {Operation(cycle).Number}; a process must flow one way.", cycle));
        }

        return problems;
    }

    /// <summary>Everything that stops a release: the structural problems, then anything incomplete.</summary>
    public IReadOnlyList<ProcessProblem> ReleaseProblems()
    {
        var problems = StructuralProblems().ToList();
        if (Operations.Count == 0)
        {
            problems.Add(new("no_operations", "Add at least one operation."));
            return problems;
        }

        foreach (var op in Operations)
        {
            if (op.Equipment.Count == 0)
            {
                problems.Add(new("no_equipment", $"Operation {op.Number} needs at least one line or department.", op.Id));
            }
            else if (op.Equipment.Sum(e => e.SharePercent) != 100m)
            {
                problems.Add(new(
                    "share_total",
                    $"Operation {op.Number}: shares add up to {op.Equipment.Sum(e => e.SharePercent):0.##}%, not 100%.",
                    op.Id));
            }
        }

        if (Operations.Count > 1 && !IsConnected())
        {
            problems.Add(new("disconnected", "Some operations aren't linked to the rest. Link them, or make them a separate process."));
        }

        return problems;
    }

    /// <summary>True when adding a link from <paramref name="from"/> to <paramref name="to"/> would loop back.</summary>
    public bool WouldLoop(Guid from, Guid to) => from == to || Reaches(to, from);

    /// <summary>Operations in an order where every operation comes after the ones it follows (by number among equals).</summary>
    public IReadOnlyList<ProcessOperation> InFlowOrder()
    {
        var incoming = Operations.ToDictionary(o => o.Id, o => Dependencies.Count(d => d.ToOperationId == o.Id));
        var ready = new SortedSet<(int Number, Guid Id)>(Operations.Where(o => incoming[o.Id] == 0).Select(o => (o.Number, o.Id)));
        var order = new List<ProcessOperation>(Operations.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            order.Add(Operation(next.Id));
            foreach (var link in Dependencies.Where(d => d.FromOperationId == next.Id))
            {
                if (--incoming[link.ToOperationId] == 0)
                {
                    ready.Add((Operation(link.ToOperationId).Number, link.ToOperationId));
                }
            }
        }

        return order;
    }

    private ProcessOperation Operation(Guid id) => Operations.First(o => o.Id == id);

    private bool Reaches(Guid from, Guid target)
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
                foreach (var link in Dependencies.Where(d => d.FromOperationId == at))
                {
                    stack.Push(link.ToOperationId);
                }
            }
        }

        return false;
    }

    /// <summary>An operation on or after a loop, or null when the links flow one way.</summary>
    private Guid? FindCycle()
    {
        var ordered = InFlowOrder().Select(o => o.Id).ToHashSet();
        return ordered.Count == Operations.Count ? null : Operations.First(o => !ordered.Contains(o.Id)).Id;
    }

    private bool IsConnected()
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>([Operations[0].Id]);
        while (stack.TryPop(out var at))
        {
            if (seen.Add(at))
            {
                foreach (var link in Dependencies.Where(d => d.FromOperationId == at || d.ToOperationId == at))
                {
                    stack.Push(link.FromOperationId == at ? link.ToOperationId : link.FromOperationId);
                }
            }
        }

        return seen.Count == Operations.Count;
    }
}
