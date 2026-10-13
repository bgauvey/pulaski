namespace Pulaski.Domain.EquipmentModel;

/// <summary>
/// ISA-95 equipment levels and the roles allowed at each. The database enforces the same
/// pairs in <c>equipment_level_role_ck</c>; keep the two in step.
/// </summary>
public static class EquipmentTypes
{
    public const string Enterprise = "enterprise";
    public const string Site = "site";
    public const string Area = "area";
    public const string WorkCenter = "work_center";
    public const string WorkUnit = "work_unit";
    public const string EquipmentModule = "equipment_module";
    public const string ControlModule = "control_module";

    /// <summary>Levels from the top of the hierarchy down.</summary>
    public static IReadOnlyList<string> Levels { get; } =
        [Enterprise, Site, Area, WorkCenter, WorkUnit, EquipmentModule, ControlModule];

    /// <summary>Every allowed (level, role) pair, in hierarchy order.</summary>
    public static IReadOnlyList<(string Level, string Role)> LevelRoles { get; } =
    [
        (Enterprise, "enterprise"),
        (Site, "site"),
        (Area, "area"),
        (WorkCenter, "production_line"),
        (WorkCenter, "process_cell"),
        (WorkCenter, "production_unit"),
        (WorkCenter, "storage_zone"),
        (WorkUnit, "work_cell"),
        (WorkUnit, "unit"),
        (WorkUnit, "machine"),
        (WorkUnit, "storage_unit"),
        (EquipmentModule, "machine"),
        (EquipmentModule, "feed_point"),
        (EquipmentModule, "module"),
        (ControlModule, "module"),
    ];

    public static bool IsValidLevelRole(string level, string role) => LevelRoles.Contains((level, role));

    /// <summary>A site carries the IANA time zone its shifts and reports use.</summary>
    public static bool RequiresTimeZone(string level) => level == Site;

    /// <summary>Only the enterprise has no parent.</summary>
    public static bool RequiresParent(string level) => level != Enterprise;

    /// <summary>A child must sit strictly below its parent in the ISA-95 hierarchy.</summary>
    public static bool CanBeChildOf(string childLevel, string parentLevel)
    {
        var child = Levels.ToList().IndexOf(childLevel);
        var parent = Levels.ToList().IndexOf(parentLevel);
        return child >= 0 && parent >= 0 && child > parent;
    }
}
