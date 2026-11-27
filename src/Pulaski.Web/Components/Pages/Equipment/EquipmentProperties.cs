using System.Globalization;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Shifts;
using Pulaski.Web.Services;
using Pulaski.Web.Services.Localization;

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
    private static readonly string Identity = Term.Mark("Identity");
    private static readonly string Classification = Term.Mark("Classification");
    private static readonly string Capabilities = Term.Mark("Capabilities");
    private static readonly string Site = Term.Mark("Site");
    private static readonly string Status = Term.Mark("Status");
    private static readonly string Hierarchy = Term.Mark("Hierarchy");
    private static readonly string WorkingTime = Term.Mark("Working time");
    private static readonly string MachineData = Term.Mark("Machine data");

    public const string CalendarKey = "calendar";
    public const string MachineProfileKey = "machineProfile";
    public const string MicroStopKey = "microStop";
    public const string StateDerivationKey = "stateDerivation";
    public const string ConstraintKey = "constraintMachine";
    public const string ProductionCounterKey = "productionCounter";

    /// <summary>The rows saved through the equipment's machine data (D-050), apart from the rest of it.</summary>
    public static IReadOnlySet<string> MachineDataKeys { get; } = new HashSet<string>([MachineProfileKey, MicroStopKey, StateDerivationKey, ConstraintKey, ProductionCounterKey]);
    private const string Inherit = "inherit";
    private const string AroundTheClock = "around_the_clock";
    private const string SchedulePrefix = "schedule:";

    private static readonly string CodeHelp = Term.Mark("Business key, unique within the site. Fixed once the equipment is created.");
    private static readonly string NameHelp = Term.Mark("Display name shown on screens and reports.");
    private static readonly string DescriptionHelp = Term.Mark("Optional notes about this equipment.");
    private static readonly string LevelHelp = Term.Mark("ISA-95 level. A child sits below its parent's level. Fixed once created.");
    private static readonly string RoleHelp = Term.Mark("ISA-95 role within the level. Fixed once created.");
    private static readonly string TracksStateHelp = Term.Mark("Collects PackML machine state, so downtime and OEE are recorded for it.");
    private static readonly string SchedulableHelp = Term.Mark("Jobs can be planned to it. A schedulable group such as a department, without Can run jobs, takes jobs in a time window and its lines run them.");

    private static readonly string CanRunJobHelp = Term.Mark("Jobs actually run here: a line or cell. Jobs planned to a schedulable group above it can be released to it.");
    private static readonly string HoldsInventoryHelp = Term.Mark("Material lots can be located here.");
    private static readonly string ComminglesLotsHelp = Term.Mark("Lots here mix and can't be told apart, like a silo or tank: everything received, moved or produced here joins one balance per material, with no lot number. A numbered lot is kept for its genealogy, then merged in.");

    private static readonly string SingleMaterialHelp = Term.Mark("Takes one material at a time, like a silo for one resin: while one material is here, nothing of another can arrive. Once it is empty, any material can go in.");
    private static readonly string TimeZoneHelp = Term.Mark("IANA time zone the site's shifts and reports use, e.g. America/Chicago.");

    // ---- Existing equipment ----

    /// <summary>
    /// The rows for existing equipment. Rows the user may not change are shown locked: <paramref name="canEdit"/> for the
    /// equipment itself, <paramref name="canSetShifts"/> for the shift schedule it follows (D-037, D-038).
    /// </summary>
    public static IReadOnlyList<PropertyItem> ForExisting(Text l,
        EquipmentDto e, EquipmentTree tree, IReadOnlyList<ShiftScheduleDto>? schedules = null, bool canEdit = true, bool canSetShifts = true,
        IReadOnlyList<MachineProfileDto>? profiles = null)
    {
        var parent = e.ParentId is { } parentId ? tree.Find(parentId) : null;
        var site = e.SiteId is { } siteId ? tree.Find(siteId) : null;

        List<PropertyItem> items =
        [
            new() { Key = "code", Name = l["Code"], Category = l[Identity], Description = l[CodeHelp], Value = e.Code },
            new() { Key = "name", Name = l["Name"], Category = l[Identity], Description = l[NameHelp], Value = e.Name, Editor = PropertyEditor.Text, MaxLength = 200 },
            new() { Key = "description", Name = l["Description"], Category = l[Identity], Description = l[DescriptionHelp], Value = e.Description, Editor = PropertyEditor.Text },
            new() { Key = "level", Name = l["Level"], Category = l[Classification], Description = l[LevelHelp], Value = Display.Label(e.Level) },
            new() { Key = "role", Name = l["Role"], Category = l[Classification], Description = l[RoleHelp], Value = Display.Label(e.EquipmentRole) },
            Bool("tracksState", l["Tracks state"], l[Capabilities], l[TracksStateHelp], e.TracksState),
            Bool("isSchedulable", l["Schedulable"], l[Capabilities], l[SchedulableHelp], e.IsSchedulable),
            Bool("canRunJob", l["Can run jobs"], l[Capabilities], l[CanRunJobHelp], e.CanRunJob),
            Bool("holdsInventory", l["Holds inventory"], l[Capabilities], l[HoldsInventoryHelp], e.HoldsInventory),
        ];

        if (e.HoldsInventory)
        {
            items.Add(Bool("comminglesLots", l["Lots are indistinguishable"], l[Capabilities], l[ComminglesLotsHelp], e.ComminglesLots));
            items.Add(Bool("singleMaterial", l["One material at a time"], l[Capabilities], l[SingleMaterialHelp], e.SingleMaterial));
        }

        if (e.Level == "site")
        {
            items.Add(new()
            {
                Key = "timeZone",
                Name = l["Time zone"],
                Category = l[Site],
                Description = l["IANA time zone the site's shifts and reports use, e.g. America/Chicago. Set when the site is created."],
                Value = e.TimeZone,
            });
        }

        items.Add(Bool("isActive", l["Active"], l[Status],
            l["Inactive equipment stays in the hierarchy and its history, grayed out."], e.IsActive));

        if (e.TracksState)
        {
            items.AddRange(MachineDataItems(l, e, tree, profiles ?? []));
        }

        if (e.CanRunJob)
        {
            items.Add(ProductionCounterItem(l, e, tree));
        }

        if (!canEdit)
        {
            items = [.. items.Select(i => i.Editor == PropertyEditor.ReadOnly ? i : i with { Locked = true })];
        }

        items.Add(CalendarItem(l, e, schedules ?? [], canSetShifts));
        items.Add(new()
        {
            Key = "calendarInEffect",
            Name = l["Shifts in effect"],
            Category = l[WorkingTime],
            Description = l["The working time planning uses for this equipment and everything below it that inherits: its own setting, or the nearest one above it."],
            Value = InEffect(l, e, tree, schedules ?? []),
        });

        items.Add(new() { Key = "parent", Name = l["Parent"], Category = l[Hierarchy], Description = l["The equipment this sits under."], Value = parent is null ? l["None"] : $"{parent.Code} · {parent.Name}" });
        items.Add(new() { Key = "site", Name = l["Owning site"], Category = l[Hierarchy], Description = l["The site whose time zone and codes this equipment uses."], Value = site is null ? l["None"] : site.Code });
        items.Add(new() { Key = "children", Name = l["Children"], Category = l[Hierarchy], Description = l["Equipment directly below this."], Value = tree.ChildrenOf(e.Id).Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        items.Add(new() { Key = "id", Name = l["Id"], Category = l[Hierarchy], Description = l["Pulaski's internal identifier, for API calls and support."], Value = e.Id.ToString() });

        return items;
    }

    /// <summary>The machine data rows (D-050): its profile, where its state comes from, and its micro-stop threshold.</summary>
    private static IEnumerable<PropertyItem> MachineDataItems(Text l, EquipmentDto e, EquipmentTree tree, IReadOnlyList<MachineProfileDto> profiles)
    {
        var own = profiles.Where(p => p.SiteId == e.SiteId && (p.IsActive || p.Id == e.MachineProfileId));
        yield return new()
        {
            Key = MachineProfileKey,
            Name = l["Machine profile"],
            Category = l[MachineData],
            Description = l["How its controller reports state and stop codes: the profile maps its state values to PackML states and its stop codes to utilization reasons. Profiles are kept on Configure → Machine profiles."],
            Value = e.MachineProfileId?.ToString() ?? "",
            Editor = PropertyEditor.Choice,
            Choices = [new("", l["None"]), .. own.Select(p => new PropertyChoice(p.Id.ToString(), $"{p.Code} · {p.Name}{(p.IsActive ? "" : " " + l["(switched off)"])}"))],
        };
        yield return new()
        {
            Key = MicroStopKey,
            Name = l["Micro-stop threshold"],
            Category = l[MachineData],
            Description = l["Stops shorter than this are micro-stops: logged, but not queued for a reason and not counted against availability; the output they cost shows as lower performance. An extruder may use 2 minutes, a pack cell more."],
            Value = Minutes(e.MicroStopThresholdSeconds),
            Editor = PropertyEditor.Number,
            Min = 0,
            Max = 60,
            Step = 0.5,
            Suffix = "min",
        };
        yield return new()
        {
            Key = StateDerivationKey,
            Name = l["State from"],
            Category = l[MachineData],
            Description = l["Where its state comes from: its own state tag, or for a line, its machines: the worst of their states when they run one after another (series), the best when they run side by side (parallel), or one constraint machine."],
            Value = e.StateDerivation,
            Editor = PropertyEditor.Choice,
            Choices =
            [
                new("own_tags", l["Its own state tag"]),
                new("series", l["Worst of its machines (series)"]),
                new("parallel", l["Best of its machines (parallel)"]),
                new("constraint_machine", l["Its constraint machine"]),
            ],
        };

        if (e.StateDerivation == "constraint_machine")
        {
            yield return new()
            {
                Key = ConstraintKey,
                Name = l["Constraint machine"],
                Category = l[MachineData],
                Description = l["The machine below it whose state is its own."],
                Value = e.ConstraintEquipmentId?.ToString() ?? "",
                Editor = PropertyEditor.Choice,
                Choices = [.. ConstraintCandidates(e, tree).Select(c => new PropertyChoice(c.Id.ToString(), $"{c.Code} · {c.Name}"))],
            };
        }
    }

    /// <summary>
    /// Where a line's output is counted for production reports from the machine (#27, D-097): the line itself or a machine below it. Left
    /// blank, the line's own processed counter is used, else the only machine below with one.
    /// </summary>
    private static PropertyItem ProductionCounterItem(Text l, EquipmentDto e, EquipmentTree tree) => new()
    {
        Key = ProductionCounterKey,
        Name = l["Production counted at"],
        Category = l[MachineData],
        Description = l["Whose processed and defective counters a production report from the machine takes, usually the last machine of the line. Found by itself when the line has its own counter or only one machine below it has one."],
        Value = e.ProductionCounterEquipmentId?.ToString() ?? "",
        Editor = PropertyEditor.Choice,
        Choices =
        [
            new("", l["Found from the counter tags"]),
            new(e.Id.ToString(), l["{0} itself", e.Code]),
            .. Below(e, tree).Select(c => new PropertyChoice(c.Id.ToString(), $"{c.Code} · {c.Name}")),
        ],
    };

    /// <summary>Everything below <paramref name="e"/>, in tree order.</summary>
    public static IReadOnlyList<EquipmentDto> Below(EquipmentDto e, EquipmentTree tree)
    {
        var found = new List<EquipmentDto>();
        void Walk(Guid id)
        {
            foreach (var child in tree.ChildrenOf(id))
            {
                found.Add(child);
                Walk(child.Id);
            }
        }

        Walk(e.Id);
        return found;
    }

    /// <summary>Equipment below <paramref name="e"/> that tracks state, in tree order.</summary>
    public static IReadOnlyList<EquipmentDto> ConstraintCandidates(EquipmentDto e, EquipmentTree tree) => [.. Below(e, tree).Where(c => c.TracksState)];

    /// <summary>
    /// The machine data request for an edit of one of the <see cref="MachineDataKeys"/> rows, or null if the value can't be read.
    /// Choosing a constraint machine line picks its first machine that tracks state; the row then lets it be changed.
    /// </summary>
    public static SetEquipmentMachineDataRequest? MachineDataChange(EquipmentDto e, PropertyChange change, EquipmentTree tree)
    {
        var request = new SetEquipmentMachineDataRequest
        {
            Version = e.Version,
            MachineProfileId = e.MachineProfileId,
            StateDerivation = e.StateDerivation,
            ConstraintEquipmentId = e.ConstraintEquipmentId,
            MicroStopThresholdSeconds = e.MicroStopThresholdSeconds,
            ProductionCounterEquipmentId = e.ProductionCounterEquipmentId,
        };

        switch (change.Key)
        {
            case MachineProfileKey:
                return request with { MachineProfileId = Guid.TryParse(change.Value, out var profile) ? profile : null };
            case MicroStopKey:
                return double.TryParse(change.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                    ? request with { MicroStopThresholdSeconds = (int)Math.Round(minutes * 60) }
                    : null;
            case StateDerivationKey:
                var derivation = change.Value ?? "own_tags";
                return request with
                {
                    StateDerivation = derivation,
                    ConstraintEquipmentId = derivation == "constraint_machine" ? e.ConstraintEquipmentId ?? (ConstraintCandidates(e, tree) is [var first, ..] ? first.Id : null) : null,
                };
            case ProductionCounterKey:
                return request with { ProductionCounterEquipmentId = Guid.TryParse(change.Value, out var counter) ? counter : null };
            case ConstraintKey:
                return Guid.TryParse(change.Value, out var machine) ? request with { ConstraintEquipmentId = machine } : null;
            default:
                return null;
        }
    }

    /// <summary>Seconds as minutes for the grid: 300 → "5", 90 → "1.5".</summary>
    public static string Minutes(int seconds) => (seconds / 60.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The working-time request for an edit of the <see cref="CalendarKey"/> row.</summary>
    public static SetEquipmentCalendarRequest CalendarChange(EquipmentDto e, string? value) =>
        value is { } v && v.StartsWith(SchedulePrefix, StringComparison.Ordinal) && Guid.TryParse(v[SchedulePrefix.Length..], out var id)
            ? new SetEquipmentCalendarRequest { Version = e.Version, Mode = "schedule", ShiftScheduleId = id }
            : new SetEquipmentCalendarRequest { Version = e.Version, Mode = value == AroundTheClock ? AroundTheClock : Inherit };

    /// <summary>
    /// Where the working time of <paramref name="e"/> comes from (D-038): the nearest equipment, itself first, that
    /// doesn't inherit; around the clock when nothing above sets one.
    /// </summary>
    public static string InEffect(Text l, EquipmentDto e, EquipmentTree tree, IReadOnlyList<ShiftScheduleDto> schedules)
    {
        for (var current = e; current is not null; current = current.ParentId is { } parentId ? tree.Find(parentId) : null)
        {
            var from = current.Id == e.Id ? "" : l[", from {0}", current.Code];
            switch (current.CalendarMode)
            {
                case AroundTheClock:
                    return l["Around the clock"] + from;
                case "schedule":
                    var schedule = schedules.FirstOrDefault(s => s.Id == current.ShiftScheduleId);
                    return (schedule is null ? l["A shift schedule"] : $"{schedule.Code} · {schedule.Name}") + from;
            }
        }

        return l["Around the clock: no shift schedule is set here or above"];
    }

    private static PropertyItem CalendarItem(Text l, EquipmentDto e, IReadOnlyList<ShiftScheduleDto> schedules, bool canSetShifts)
    {
        var value = e.CalendarMode switch
        {
            "schedule" => SchedulePrefix + e.ShiftScheduleId,
            AroundTheClock => AroundTheClock,
            _ => Inherit,
        };

        // A site's schedules are for that site and what is below it; a switched-off one stays listed while it is chosen.
        var own = e.SiteId is { } siteId
            ? schedules.Where(s => s.SiteId == siteId && (s.IsActive || s.Id == e.ShiftScheduleId))
            : [];
        List<PropertyChoice> choices =
        [
            new(Inherit, e.ParentId is null ? l["Inherit (nothing above: around the clock)"] : l["Inherit from parent"]),
            new(AroundTheClock, "Around the clock"),
            .. own.Select(s => new PropertyChoice(SchedulePrefix + s.Id, $"{s.Code} · {s.Name}{(s.IsActive ? "" : " " + l["(switched off)"])}")),
        ];

        return new()
        {
            Key = CalendarKey,
            Name = l["Shifts"],
            Category = l[WorkingTime],
            Description = l["When it works, for planning: inherit its parent's shifts, follow one of the site's shift schedules, or run around the clock. Schedules are kept on Configure → Shifts."],
            Value = value,
            Editor = PropertyEditor.Choice,
            Choices = choices,
            Locked = !canSetShifts,
        };
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

    public static IReadOnlyList<PropertyItem> ForDraft(Text l, EquipmentDraft d, IReadOnlyList<EquipmentTypeDto> types)
    {
        List<PropertyItem> items =
        [
            new() { Key = "code", Name = l["Code"], Category = l[Identity], Description = l[CodeHelp], Value = d.Code, Editor = PropertyEditor.Text, Placeholder = l["Required"], MaxLength = 50 },
            new() { Key = "name", Name = l["Name"], Category = l[Identity], Description = l[NameHelp], Value = d.Name, Editor = PropertyEditor.Text, Placeholder = l["Required"], MaxLength = 200 },
            new() { Key = "description", Name = l["Description"], Category = l[Identity], Description = l[DescriptionHelp], Value = d.Description, Editor = PropertyEditor.Text },
            new()
            {
                Key = "level", Name = l["Level"], Category = l[Classification], Description = l[LevelHelp], Value = d.Level,
                Editor = PropertyEditor.Choice,
                Choices = [.. LevelsUnder(d.Parent, types).Select(l => new PropertyChoice(l, Display.Label(l)))],
            },
            new()
            {
                Key = "role", Name = l["Role"], Category = l[Classification], Description = l["ISA-95 role within the level. Fixed once created. Changing it resets the capabilities to the role's defaults."], Value = d.Role,
                Editor = PropertyEditor.Choice,
                Choices = [.. RolesFor(d.Level, types).Select(r => new PropertyChoice(r, Display.Label(r)))],
            },
            Bool("tracksState", l["Tracks state"], l[Capabilities], l[TracksStateHelp], d.TracksState),
            Bool("isSchedulable", l["Schedulable"], l[Capabilities], l[SchedulableHelp], d.IsSchedulable),
            Bool("canRunJob", l["Can run jobs"], l[Capabilities], l[CanRunJobHelp], d.CanRunJob),
            Bool("holdsInventory", l["Holds inventory"], l[Capabilities], l[HoldsInventoryHelp], d.HoldsInventory),
        ];

        if (d.HoldsInventory)
        {
            items.Add(Bool("comminglesLots", l["Lots are indistinguishable"], l[Capabilities], l[ComminglesLotsHelp], d.ComminglesLots));
            items.Add(Bool("singleMaterial", l["One material at a time"], l[Capabilities], l[SingleMaterialHelp], d.SingleMaterial));
        }

        if (d.Level == "site")
        {
            items.Add(new()
            {
                Key = "timeZone",
                Name = l["Time zone"],
                Category = l[Site],
                Description = l[TimeZoneHelp],
                Value = d.TimeZone,
                Editor = PropertyEditor.Text,
                Placeholder = l["Required, e.g. America/Chicago"],
            });
        }

        items.Add(new() { Key = "parent", Name = l["Parent"], Category = l[Hierarchy], Description = l["The equipment this will sit under."], Value = d.Parent is null ? l["None"] : $"{d.Parent.Code} · {d.Parent.Name}" });
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
