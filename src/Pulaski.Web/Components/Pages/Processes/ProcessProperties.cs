using System.Globalization;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Web.Services;
using Pulaski.Web.Services.Processes;

namespace Pulaski.Web.Components.Pages.Processes;

/// <summary>
/// Property grid rows for a process, an operation and a link (D-023), and the keys the editor
/// applies changes by. New properties go here: add a row with its key, then handle the key.
/// </summary>
public static class ProcessProperties
{
    // Process
    public const string Name = "name";
    public const string Description = "description";
    public const string IsActive = "isActive";
    public const string AllMaterials = "allMaterials";
    public const string ClassPrefix = "class:";
    public const string MaterialPrefix = "material:";
    public const string DefaultPrefix = "default:";

    // Operation
    public const string Number = "number";
    public const string SharePrefix = "share:";

    // Link
    public const string LinkType = "type";
    public const string Lag = "lag";

    private const string Identity = "Identity";
    private const string Status = "Status";
    private const string Makes = "Makes";
    private const string DefaultFor = "Default for";
    private const string RunsOn = "Runs on";
    private const string Flow = "Flow";
    private const string Link = "Link";

    public static IReadOnlyList<PropertyItem> ForProcess(
        ProcessDto process, ProcessVersionDto version, IReadOnlyList<MaterialDto> materials)
    {
        var items = new List<PropertyItem>
        {
            new() { Key = "code", Name = "Code", Category = Identity, Value = process.Code, Description = "Unique within the site. Fixed once created." },
            new() { Key = Name, Name = "Name", Category = Identity, Value = process.Name, Editor = PropertyEditor.Text, MaxLength = 200, Description = "Display name, e.g. Extrude and Pack." },
            new() { Key = Description, Name = "Description", Category = Identity, Value = process.Description, Editor = PropertyEditor.Text, Placeholder = "Optional", Description = "Notes about the process." },
            new() { Key = "site", Name = "Site", Category = Identity, Value = process.SiteCode, Description = "The site whose lines and departments it can use." },
            new()
            {
                Key = IsActive, Name = "Active", Category = Status, Value = PropertyItem.FromBool(process.IsActive),
                Editor = PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = "An inactive process can't be picked for new orders or be a material's default.",
            },
            new() { Key = "version", Name = "Version shown", Category = Status, Value = $"{version.Number} · {Display.Label(version.Status)}", Description = "The version on the canvas. Pick another above the canvas." },
            new()
            {
                Key = "released", Name = "Released", Category = Status,
                Value = version.ReleasedAt is { } at ? at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "Not yet",
                Description = version.Note is { } note ? $"Release note: {note}" : "When this version was released for orders.",
            },
            new()
            {
                Key = AllMaterials, Name = "All materials", Category = Makes, Value = PropertyItem.FromBool(process.MakesAllMaterials),
                Editor = PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = "True: it can make any material. False: only the classes and materials listed below. Order creation offers it for any material it makes.",
            },
        };

        if (!process.MakesAllMaterials)
        {
            items.AddRange(process.MakesClasses.Select(c => new PropertyItem
            {
                Key = ClassPrefix + c.Id,
                Name = $"Class {c.Code}",
                Category = Makes,
                Value = c.Name,
                Removable = true,
                Description = $"It makes every material in the {c.Code} class.",
            }));
            items.AddRange(process.MakesMaterials.Select(m => new PropertyItem
            {
                Key = MaterialPrefix + m.Id,
                Name = m.Code,
                Category = Makes,
                Value = m.Name,
                Removable = true,
                Description = $"It makes {m.Code}.",
            }));
            if (process.MakesClasses.Count == 0 && process.MakesMaterials.Count == 0)
            {
                items.Add(new() { Key = "makesNothing", Name = "Classes and materials", Category = Makes, Value = "None yet", IsFlagged = true, Description = "It makes nothing yet, so order creation won't offer it. Add a class or a material below." });
            }
        }

        foreach (var material in process.DefaultFor)
        {
            var covered = materials.FirstOrDefault(m => m.Id == material.Id) is { } known && Covers(process, known);
            items.Add(new()
            {
                Key = DefaultPrefix + material.Id,
                Name = material.Code,
                Category = DefaultFor,
                Removable = true,
                Value = covered ? material.Name : $"{material.Name} (not made)",
                IsFlagged = !covered,
                Description = covered
                    ? $"Order creation pre-selects this process for {material.Code}."
                    : $"The process no longer makes {material.Code}, so order creation won't offer it.",
            });
        }

        return items;
    }

    public static IReadOnlyList<PropertyItem> ForOperation(
        ProcessOperationDto op, ProcessDefinitionDto definition, IReadOnlyList<EquipmentDto> equipment, bool editable)
    {
        var editor = editable ? PropertyEditor.Text : PropertyEditor.ReadOnly;
        var items = new List<PropertyItem>
        {
            new()
            {
                Key = Number, Name = "Number", Category = Identity, Value = op.Number.ToString(CultureInfo.InvariantCulture),
                Editor = editable ? PropertyEditor.Number : PropertyEditor.ReadOnly, Min = 1, Step = 10,
                Description = "Its place in the route: 10, 20, 30… Unique within the version.",
            },
            new() { Key = Name, Name = "Name", Category = Identity, Value = op.Name, Editor = editor, MaxLength = 200, Description = "What it does, e.g. Extrude." },
            new() { Key = Description, Name = "Description", Category = Identity, Value = op.Description, Editor = editor, Placeholder = editable ? "Optional" : null, Description = "Notes for planners and operators." },
        };

        foreach (var use in op.Equipment)
        {
            var eq = equipment.FirstOrDefault(e => e.Id == use.EquipmentId);
            var kind = eq is { IsSchedulable: true, CanRunJob: false } ? "Department" : "Line";
            items.Add(new()
            {
                Key = SharePrefix + use.EquipmentId,
                Name = eq?.Code ?? "Unknown equipment",
                Category = RunsOn,
                Value = use.SharePercent.ToString("0.##", CultureInfo.InvariantCulture),
                Suffix = "%",
                Editor = editable ? PropertyEditor.Number : PropertyEditor.ReadOnly,
                Min = 0.01,
                Max = 100,
                Step = 0.01,
                Removable = editable,
                Description = $"{kind}{(eq is null ? "" : $" · {eq.Name}")}. Its share of the operation's quantity; it becomes its own job for that share.",
            });
        }

        var total = ProcessEditing.ShareTotal(op);
        items.Add(new()
        {
            Key = "total",
            Name = "Total",
            Category = RunsOn,
            Value = op.Equipment.Count == 0 ? "No line or department yet" : total.ToString("0.##", CultureInfo.InvariantCulture) + "%",
            IsFlagged = op.Equipment.Count == 0 || total != 100m,
            Description = "The shares must add up to 100% before the version can be released.",
        });

        string Names(IEnumerable<ProcessDependencyDto> links, Func<ProcessDependencyDto, Guid> other) =>
            string.Join(", ", links.Select(l => definition.Operations.FirstOrDefault(o => o.Id == other(l)) is { } o
                ? $"{o.Number} {o.Name} ({ProcessEditing.Describe(l)})"
                : "?"));
        var follows = definition.Dependencies.Where(d => d.ToOperationId == op.Id).ToList();
        var leads = definition.Dependencies.Where(d => d.FromOperationId == op.Id).ToList();
        items.Add(new() { Key = "follows", Name = "Follows", Category = Flow, Value = follows.Count == 0 ? "Nothing: it can start first" : Names(follows, l => l.FromOperationId), Description = "The operations it waits for. Draw links on the canvas." });
        items.Add(new() { Key = "leadsTo", Name = "Followed by", Category = Flow, Value = leads.Count == 0 ? "Nothing: it ends the route" : Names(leads, l => l.ToOperationId), Description = "The operations that wait for it." });
        return items;
    }

    public static IReadOnlyList<PropertyItem> ForLink(ProcessDependencyDto link, ProcessDefinitionDto definition, bool editable)
    {
        var from = definition.Operations.First(o => o.Id == link.FromOperationId);
        var to = definition.Operations.First(o => o.Id == link.ToOperationId);
        return
        [
            new() { Key = "from", Name = "From", Category = Link, Value = $"{from.Number} {from.Name}", Description = "The earlier operation." },
            new() { Key = "to", Name = "To", Category = Link, Value = $"{to.Number} {to.Name}", Description = "The operation that follows it." },
            new()
            {
                Key = LinkType, Name = "Starts", Category = Link, Value = link.Type,
                Editor = editable ? PropertyEditor.Choice : PropertyEditor.ReadOnly,
                Choices = [new(ProcessEditing.AfterStart, $"After {from.Name} starts"), new(ProcessEditing.AfterEnd, $"After {from.Name} ends")],
                Description = "After start: material flows straight through, so both run together. After end: a hand-off of the finished quantity.",
            },
            new()
            {
                Key = Lag, Name = "Lag", Category = Link, Value = ((int)link.Lag.TotalMinutes).ToString(CultureInfo.InvariantCulture), Suffix = "min",
                Editor = editable ? PropertyEditor.Number : PropertyEditor.ReadOnly, Min = 0, Step = 5,
                Description = "Extra wait after the start or end before the next operation begins.",
            },
        ];
    }

    /// <summary>Whether the process makes the material, as the API decides it (D-023).</summary>
    public static bool Covers(ProcessDto process, MaterialDto material) =>
        process.MakesAllMaterials
        || process.MakesMaterials.Any(m => m.Id == material.Id)
        || (material.MaterialClassId is { } classId && process.MakesClasses.Any(c => c.Id == classId));

    /// <summary>The id after a key's prefix, such as the equipment in "share:{id}".</summary>
    public static Guid? IdAfter(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(key[prefix.Length..], out var id) ? id : null;
}
