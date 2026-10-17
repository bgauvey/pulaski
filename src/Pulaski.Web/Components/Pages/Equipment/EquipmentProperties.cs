using Pulaski.Contracts.EquipmentModel;
using Pulaski.Web.Services;

namespace Pulaski.Web.Components.Pages.Equipment;

/// <summary>Equipment being added: filled in through the property grid, then created in one call.</summary>
public sealed class EquipmentDraft
{
    public EquipmentDto? Parent { get; init; }
    public string Level { get; set; } = "";
    public string Role { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? TimeZone { get; set; }
    public bool TracksState { get; set; }
    public bool IsSchedulable { get; set; }
    public bool CanRunJob { get; set; }
    public bool HoldsInventory { get; set; }
    public bool ComminglesLots { get; set; }
    public bool SingleMaterial { get; set; }

    /// <summary>A placeholder row for the tree grid.</summary>
    public EquipmentDto AsRow() => new()
    {
        Id = EquipmentTreeGrid.DraftId,
        ParentId = Parent?.Id,
        Code = Code,
        Name = Name,
        Level = Level,
        EquipmentRole = Role,
        IsActive = true,
    };

    public CreateEquipmentRequest ToRequest() => new()
    {
        ParentId = Parent?.Id,
        Code = Code.Trim(),
        Name = Name.Trim(),
        Description = EquipmentProperties.NullIfBlank(Description),
        Level = Level,
        EquipmentRole = Role,
        TracksState = TracksState,
        IsSchedulable = IsSchedulable,
        CanRunJob = CanRunJob,
        HoldsInventory = HoldsInventory,
        ComminglesLots = HoldsInventory && ComminglesLots,
        SingleMaterial = HoldsInventory && SingleMaterial,
        TimeZone = Level == "site" ? EquipmentProperties.NullIfBlank(TimeZone) : null,
    };
}

/// <summary>Builds the property grid rows for equipment and applies edits to it.</summary>
public static class EquipmentProperties
{
    private const string Identity = "Identity";
    private const string Classification = "Classification";
    private const string Capabilities = "Capabilities";
    private const string Site = "Site";
    private const string Status = "Status";
    private const string Hierarchy = "Hierarchy";

    private const string CodeHelp = "Business key, unique within the site. Fixed once the equipment is created.";
    private const string NameHelp = "Display name shown on screens and reports.";
    private const string DescriptionHelp = "Optional notes about this equipment.";
    private const string LevelHelp = "ISA-95 level. A child sits below its parent's level. Fixed once created.";
    private const string RoleHelp = "ISA-95 role within the level. Fixed once created.";
    private const string TracksStateHelp = "Collects PackML machine state, so downtime and OEE are recorded for it.";
    private const string SchedulableHelp =
        "Jobs can be planned to it. A schedulable group such as a department, without Can run jobs, takes jobs in a time window and its lines run them.";

    private const string CanRunJobHelp =
        "Jobs actually run here: a line or cell. Jobs planned to a schedulable group above it can be released to it.";
    private const string HoldsInventoryHelp = "Material lots can be located here.";
    private const string ComminglesLotsHelp =
        "Lots here mix and can't be told apart, like a silo or tank: everything received, moved or produced here joins one balance per material, with no lot number. A numbered lot is kept for its genealogy, then merged in.";

    private const string SingleMaterialHelp =
        "Takes one material at a time, like a silo for one resin: while one material is here, nothing of another can arrive. Once it is empty, any material can go in.";
    private const string TimeZoneHelp = "IANA time zone the site's shifts and reports use, e.g. America/Chicago.";

    // ---- Existing equipment ----

    public static IReadOnlyList<PropertyItem> ForExisting(EquipmentDto e, EquipmentTree tree)
    {
        var parent = e.ParentId is { } parentId ? tree.Find(parentId) : null;
        var site = e.SiteId is { } siteId ? tree.Find(siteId) : null;

        List<PropertyItem> items =
        [
            new() { Key = "code", Name = "Code", Category = Identity, Description = CodeHelp, Value = e.Code },
            new() { Key = "name", Name = "Name", Category = Identity, Description = NameHelp, Value = e.Name, Editor = PropertyEditor.Text, MaxLength = 200 },
            new() { Key = "description", Name = "Description", Category = Identity, Description = DescriptionHelp, Value = e.Description, Editor = PropertyEditor.Text },
            new() { Key = "level", Name = "Level", Category = Classification, Description = LevelHelp, Value = Display.Label(e.Level) },
            new() { Key = "role", Name = "Role", Category = Classification, Description = RoleHelp, Value = Display.Label(e.EquipmentRole) },
            Bool("tracksState", "Tracks state", Capabilities, TracksStateHelp, e.TracksState),
            Bool("isSchedulable", "Schedulable", Capabilities, SchedulableHelp, e.IsSchedulable),
            Bool("canRunJob", "Can run jobs", Capabilities, CanRunJobHelp, e.CanRunJob),
            Bool("holdsInventory", "Holds inventory", Capabilities, HoldsInventoryHelp, e.HoldsInventory),
        ];

        if (e.HoldsInventory)
        {
            items.Add(Bool("comminglesLots", "Lots are indistinguishable", Capabilities, ComminglesLotsHelp, e.ComminglesLots));
            items.Add(Bool("singleMaterial", "One material at a time", Capabilities, SingleMaterialHelp, e.SingleMaterial));
        }

        if (e.Level == "site")
        {
            items.Add(new()
            {
                Key = "timeZone",
                Name = "Time zone",
                Category = Site,
                Description = TimeZoneHelp + " Set when the site is created.",
                Value = e.TimeZone,
            });
        }

        items.Add(Bool("isActive", "Active", Status,
            "Inactive equipment stays in the hierarchy and its history, greyed out.", e.IsActive));

        items.Add(new() { Key = "parent", Name = "Parent", Category = Hierarchy, Description = "The equipment this sits under.", Value = parent is null ? "None" : $"{parent.Code} · {parent.Name}" });
        items.Add(new() { Key = "site", Name = "Owning site", Category = Hierarchy, Description = "The site whose time zone and codes this equipment uses.", Value = site is null ? "None" : site.Code });
        items.Add(new() { Key = "children", Name = "Children", Category = Hierarchy, Description = "Equipment directly below this.", Value = tree.ChildrenOf(e.Id).Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        items.Add(new() { Key = "id", Name = "Id", Category = Hierarchy, Description = "Pulaski's internal identifier, for API calls and support.", Value = e.Id.ToString() });

        return items;
    }

    /// <summary>The update to send for one edited property, or null if the key is not editable.</summary>
    public static UpdateEquipmentRequest? ApplyChange(EquipmentDto e, PropertyChange change)
    {
        var request = new UpdateEquipmentRequest
        {
            Version = e.Version,
            Name = e.Name,
            Description = e.Description,
            TracksState = e.TracksState,
            IsSchedulable = e.IsSchedulable,
            CanRunJob = e.CanRunJob,
            HoldsInventory = e.HoldsInventory,
            ComminglesLots = e.ComminglesLots,
            SingleMaterial = e.SingleMaterial,
            IsActive = e.IsActive,
        };

        return change.Key switch
        {
            "name" => request with { Name = (change.Value ?? "").Trim() },
            "description" => request with { Description = NullIfBlank(change.Value) },
            "tracksState" => request with { TracksState = change.Value == "true" },
            "isSchedulable" => request with { IsSchedulable = change.Value == "true" },
            "canRunJob" => request with { CanRunJob = change.Value == "true" },
            // Lots can only mix where inventory is held, so dropping one drops the other.
            "holdsInventory" => request with
            {
                HoldsInventory = change.Value == "true",
                ComminglesLots = change.Value == "true" && e.ComminglesLots,
                SingleMaterial = change.Value == "true" && e.SingleMaterial,
            },
            "comminglesLots" => request with { ComminglesLots = change.Value == "true" },
            "singleMaterial" => request with { SingleMaterial = change.Value == "true" },
            "isActive" => request with { IsActive = change.Value == "true" },
            _ => null,
        };
    }

    // ---- New equipment ----

    /// <summary>A draft under <paramref name="parent"/> (the enterprise when null), or null if nothing can go there.</summary>
    public static EquipmentDraft? NewDraft(EquipmentDto? parent, IReadOnlyList<EquipmentTypeDto> types)
    {
        if (LevelsUnder(parent, types) is not [var level, ..])
        {
            return null;
        }

        var draft = new EquipmentDraft { Parent = parent };
        SetLevel(draft, level, types);
        return draft;
    }

    /// <summary>Levels a child of <paramref name="parent"/> may take; types arrive in hierarchy order.</summary>
    public static IReadOnlyList<string> LevelsUnder(EquipmentDto? parent, IReadOnlyList<EquipmentTypeDto> types)
    {
        var levels = types.Select(t => t.Level).Distinct().ToList();
        return parent is null ? [.. levels.Take(1)] : [.. levels.Skip(levels.IndexOf(parent.Level) + 1)];
    }

    public static IReadOnlyList<PropertyItem> ForDraft(EquipmentDraft d, IReadOnlyList<EquipmentTypeDto> types)
    {
        List<PropertyItem> items =
        [
            new() { Key = "code", Name = "Code", Category = Identity, Description = CodeHelp, Value = d.Code, Editor = PropertyEditor.Text, Placeholder = "Required", MaxLength = 50 },
            new() { Key = "name", Name = "Name", Category = Identity, Description = NameHelp, Value = d.Name, Editor = PropertyEditor.Text, Placeholder = "Required", MaxLength = 200 },
            new() { Key = "description", Name = "Description", Category = Identity, Description = DescriptionHelp, Value = d.Description, Editor = PropertyEditor.Text },
            new()
            {
                Key = "level", Name = "Level", Category = Classification, Description = LevelHelp, Value = d.Level,
                Editor = PropertyEditor.Choice,
                Choices = [.. LevelsUnder(d.Parent, types).Select(l => new PropertyChoice(l, Display.Label(l)))],
            },
            new()
            {
                Key = "role", Name = "Role", Category = Classification, Description = RoleHelp + " Changing it resets the capabilities to the role's defaults.", Value = d.Role,
                Editor = PropertyEditor.Choice,
                Choices = [.. RolesFor(d.Level, types).Select(r => new PropertyChoice(r, Display.Label(r)))],
            },
            Bool("tracksState", "Tracks state", Capabilities, TracksStateHelp, d.TracksState),
            Bool("isSchedulable", "Schedulable", Capabilities, SchedulableHelp, d.IsSchedulable),
            Bool("canRunJob", "Can run jobs", Capabilities, CanRunJobHelp, d.CanRunJob),
            Bool("holdsInventory", "Holds inventory", Capabilities, HoldsInventoryHelp, d.HoldsInventory),
        ];

        if (d.HoldsInventory)
        {
            items.Add(Bool("comminglesLots", "Lots are indistinguishable", Capabilities, ComminglesLotsHelp, d.ComminglesLots));
            items.Add(Bool("singleMaterial", "One material at a time", Capabilities, SingleMaterialHelp, d.SingleMaterial));
        }

        if (d.Level == "site")
        {
            items.Add(new()
            {
                Key = "timeZone",
                Name = "Time zone",
                Category = Site,
                Description = TimeZoneHelp,
                Value = d.TimeZone,
                Editor = PropertyEditor.Text,
                Placeholder = "Required, e.g. America/Chicago",
            });
        }

        items.Add(new() { Key = "parent", Name = "Parent", Category = Hierarchy, Description = "The equipment this will sit under.", Value = d.Parent is null ? "None" : $"{d.Parent.Code} · {d.Parent.Name}" });
        return items;
    }

    public static void ApplyChange(EquipmentDraft d, PropertyChange change, IReadOnlyList<EquipmentTypeDto> types)
    {
        switch (change.Key)
        {
            case "code":
                d.Code = change.Value ?? "";
                break;
            case "name":
                d.Name = change.Value ?? "";
                break;
            case "description":
                d.Description = change.Value;
                break;
            case "timeZone":
                d.TimeZone = change.Value;
                break;
            case "level":
                SetLevel(d, change.Value ?? "", types);
                break;
            case "role":
                SetRole(d, change.Value ?? "");
                break;
            case "tracksState":
                d.TracksState = change.Value == "true";
                break;
            case "isSchedulable":
                d.IsSchedulable = change.Value == "true";
                break;
            case "canRunJob":
                d.CanRunJob = change.Value == "true";
                break;
            case "holdsInventory":
                d.HoldsInventory = change.Value == "true";
                d.ComminglesLots &= d.HoldsInventory;
                d.SingleMaterial &= d.HoldsInventory;
                break;
            case "comminglesLots":
                d.ComminglesLots = change.Value == "true";
                break;
            case "singleMaterial":
                d.SingleMaterial = change.Value == "true";
                break;
        }
    }

    internal static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<string> RolesFor(string level, IReadOnlyList<EquipmentTypeDto> types) =>
        types.Where(t => t.Level == level).Select(t => t.Role);

    private static void SetLevel(EquipmentDraft d, string level, IReadOnlyList<EquipmentTypeDto> types)
    {
        d.Level = level;
        SetRole(d, RolesFor(level, types).FirstOrDefault() ?? "");
    }

    // Sensible capability defaults per role; the user can still change them.
    private static void SetRole(EquipmentDraft d, string role)
    {
        d.Role = role;
        d.TracksState = role is "production_line" or "machine" or "unit" or "work_cell" or "process_cell";
        d.IsSchedulable = role is "production_line" or "process_cell" or "production_unit";
        d.CanRunJob = d.IsSchedulable;
        d.HoldsInventory = role is "feed_point" or "storage_zone" or "storage_unit";
        d.ComminglesLots = false;
        d.SingleMaterial = false;
    }

    private static PropertyItem Bool(string key, string name, string category, string description, bool value) => new()
    {
        Key = key,
        Name = name,
        Category = category,
        Description = description,
        Value = PropertyItem.FromBool(value),
        Editor = PropertyEditor.Choice,
        Choices = PropertyChoice.Boolean,
    };
}
