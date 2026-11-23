using System.Globalization;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Web.Services.Localization;
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

    private static readonly string Identity = Term.Mark("Identity");
    private static readonly string Status = Term.Mark("Status");
    private static readonly string Makes = Term.Mark("Makes");
    private static readonly string DefaultFor = Term.Mark("Default for");
    private static readonly string RunsOn = Term.Mark("Runs on");
    private static readonly string Flow = Term.Mark("Flow");
    private static readonly string Link = Term.Mark("Link");

    public static IReadOnlyList<PropertyItem> ForProcess(Text l,
        ProcessDto process, ProcessVersionDto version, IReadOnlyList<MaterialDto> materials)
    {
        var items = new List<PropertyItem>
        {
            new() { Key = "code", Name = l["Code"], Category = l[Identity], Value = process.Code, Description = l["Unique within the site. Fixed once created."] },
            new() { Key = Name, Name = l["Name"], Category = l[Identity], Value = process.Name, Editor = PropertyEditor.Text, MaxLength = 200, Description = l["Display name, e.g. Extrude and Pack."] },
            new() { Key = Description, Name = l["Description"], Category = l[Identity], Value = process.Description, Editor = PropertyEditor.Text, Placeholder = l["Optional"], Description = l["Notes about the process."] },
            new() { Key = "site", Name = l["Site"], Category = l[Identity], Value = process.SiteCode, Description = l["The site whose lines and departments it can use."] },
            new()
            {
                Key = IsActive, Name = l["Active"], Category = l[Status], Value = PropertyItem.FromBool(process.IsActive),
                Editor = PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = l["An inactive process can't be picked for new orders or be a material's default."],
            },
            new() { Key = "version", Name = l["Version shown"], Category = l[Status], Value = $"{version.Number} · {l.Label(version.Status)}", Description = l["The version on the canvas. Pick another above the canvas."] },
            new()
            {
                Key = "released", Name = l["Released"], Category = l[Status],
                Value = version.ReleasedAt is { } at ? at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + l[" UTC"] : l["Not yet"],
                Description = version.Note is { } note ? l["Release note: {0}", note] : l["When this version was released for orders."],
            },
            new()
            {
                Key = AllMaterials, Name = l["All materials"], Category = l[Makes], Value = PropertyItem.FromBool(process.MakesAllMaterials),
                Editor = PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = l["True: it can make any material. False: only the classes and materials listed below. Order creation offers it for any material it makes."],
            },
        };

        if (!process.MakesAllMaterials)
        {
            items.AddRange(process.MakesClasses.Select(c => new PropertyItem
            {
                Key = ClassPrefix + c.Id,
                Name = l["Class {0}", c.Code],
                Category = l[Makes],
                Value = c.Name,
                Removable = true,
                Description = l["It makes every material in the {0} class.", c.Code],
            }));
            items.AddRange(process.MakesMaterials.Select(m => new PropertyItem
            {
                Key = MaterialPrefix + m.Id,
                Name = m.Code,
                Category = l[Makes],
                Value = m.Name,
                Removable = true,
                Description = l["It makes {0}.", m.Code],
            }));
            if (process.MakesClasses.Count == 0 && process.MakesMaterials.Count == 0)
            {
                items.Add(new() { Key = "makesNothing", Name = l["Classes and materials"], Category = l[Makes], Value = l["None yet"], IsFlagged = true, Description = l["It makes nothing yet, so order creation won't offer it. Add a class or a material below."] });
            }
        }

        foreach (var material in process.DefaultFor)
        {
            var covered = materials.FirstOrDefault(m => m.Id == material.Id) is { } known && Covers(process, known);
            items.Add(new()
            {
                Key = DefaultPrefix + material.Id,
                Name = material.Code,
                Category = l[DefaultFor],
                Removable = true,
                Value = covered ? material.Name : l["{0} (not made)", material.Name],
                IsFlagged = !covered,
                Description = covered
                    ? l["Order creation pre-selects this process for {0}.", material.Code]
                    : l["The process no longer makes {0}, so order creation won't offer it.", material.Code],
            });
        }

        return items;
    }

    public static IReadOnlyList<PropertyItem> ForOperation(Text l,
        ProcessOperationDto op, ProcessDefinitionDto definition, IReadOnlyList<EquipmentDto> equipment, bool editable)
    {
        var editor = editable ? PropertyEditor.Text : PropertyEditor.ReadOnly;
        var items = new List<PropertyItem>
        {
            new()
            {
                Key = Number, Name = l["Number"], Category = l[Identity], Value = op.Number.ToString(CultureInfo.InvariantCulture),
                Editor = editable ? PropertyEditor.Number : PropertyEditor.ReadOnly, Min = 1, Step = 10,
                Description = l["Its place in the route: 10, 20, 30… Unique within the version."],
            },
            new() { Key = Name, Name = l["Name"], Category = l[Identity], Value = op.Name, Editor = editor, MaxLength = 200, Description = l["What it does, e.g. Extrude."] },
            new() { Key = Description, Name = l["Description"], Category = l[Identity], Value = op.Description, Editor = editor, Placeholder = editable ? l["Optional"] : null, Description = l["Notes for planners and operators."] },
        };

        foreach (var use in op.Equipment)
        {
            var eq = equipment.FirstOrDefault(e => e.Id == use.EquipmentId);
            var kind = eq is { IsSchedulable: true, CanRunJob: false } ? l["Department"] : l["Line"];
            items.Add(new()
            {
                Key = SharePrefix + use.EquipmentId,
                Name = eq?.Code ?? l["Unknown equipment"],
                Category = l[RunsOn],
                Value = use.SharePercent.ToString("0.##", CultureInfo.InvariantCulture),
                Suffix = "%",
                Editor = editable ? PropertyEditor.Number : PropertyEditor.ReadOnly,
                Min = 0.01,
                Max = 100,
                Step = 0.01,
                Removable = editable,
                Description = l["{0}{1}. Its share of the operation's quantity; it becomes its own job for that share.", kind, (eq is null ? "" : $" · {eq.Name}")],
            });
        }

        var total = ProcessEditing.ShareTotal(op);
        items.Add(new()
        {
            Key = "total",
            Name = l["Total"],
            Category = l[RunsOn],
            Value = op.Equipment.Count == 0 ? l["No line or department yet"] : total.ToString("0.##", CultureInfo.InvariantCulture) + "%",
            IsFlagged = op.Equipment.Count == 0 || total != 100m,
            Description = l["The shares must add up to 100% before the version can be released."],
        });

        string Names(IEnumerable<ProcessDependencyDto> links, Func<ProcessDependencyDto, Guid> other) =>
            string.Join(", ", links.Select(d => definition.Operations.FirstOrDefault(o => o.Id == other(d)) is { } o
                ? $"{o.Number} {o.Name} ({ProcessEditing.Describe(l, d)})"
                : "?"));
        var follows = definition.Dependencies.Where(d => d.ToOperationId == op.Id).ToList();
        var leads = definition.Dependencies.Where(d => d.FromOperationId == op.Id).ToList();
        items.Add(new() { Key = "follows", Name = l["Follows"], Category = l[Flow], Value = follows.Count == 0 ? l["Nothing: it can start first"] : Names(follows, d => d.FromOperationId), Description = l["The operations it waits for. Draw links on the canvas."] });
        items.Add(new() { Key = "leadsTo", Name = l["Followed by"], Category = l[Flow], Value = leads.Count == 0 ? l["Nothing: it ends the route"] : Names(leads, d => d.ToOperationId), Description = l["The operations that wait for it."] });
        return items;
    }

    public static IReadOnlyList<PropertyItem> ForLink(Text l, ProcessDependencyDto link, ProcessDefinitionDto definition, bool editable)
    {
        var from = definition.Operations.First(o => o.Id == link.FromOperationId);
        var to = definition.Operations.First(o => o.Id == link.ToOperationId);
        return
        [
            new() { Key = "from", Name = l["From"], Category = l[Link], Value = $"{from.Number} {from.Name}", Description = l["The earlier operation."] },
            new() { Key = "to", Name = l["To"], Category = l[Link], Value = $"{to.Number} {to.Name}", Description = l["The operation that follows it."] },
            new()
            {
                Key = LinkType, Name = l["Starts"], Category = l[Link], Value = link.Type,
                Editor = editable ? PropertyEditor.Choice : PropertyEditor.ReadOnly,
                Choices = [new(ProcessEditing.AfterStart, l["After {0} starts", from.Name]), new(ProcessEditing.AfterEnd, l["After {0} ends", from.Name])],
                Description = l["After start: material flows straight through, so both run together. After end: a hand-off of the finished quantity."],
            },
            new()
            {
                Key = Lag, Name = l["Lag"], Category = l[Link], Value = ((int)link.Lag.TotalMinutes).ToString(CultureInfo.InvariantCulture), Suffix = l["min"],
                Editor = editable ? PropertyEditor.Number : PropertyEditor.ReadOnly, Min = 0, Step = 5,
                Description = l["Extra wait after the start or end before the next operation begins."],
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
