namespace Pulaski.Contracts.EquipmentModel;

public sealed record CreateEquipmentRequest
{
    public Guid? ParentId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Level { get; init; } = "";
    public string EquipmentRole { get; init; } = "";
    public bool TracksState { get; init; }
    public bool IsSchedulable { get; init; }
    /// <summary>
    /// Runs jobs (D-022). Left out, a new row runs jobs when it is schedulable, and an update
    /// keeps the current value. A schedulable row that can't run jobs is a group, such as a department.
    /// </summary>
    public bool? CanRunJob { get; init; }
    public bool HoldsInventory { get; init; }

    /// <summary>Lots here mix into one balance per material, like a silo (D-031). Needs <see cref="HoldsInventory"/>.</summary>
    public bool ComminglesLots { get; init; }
    public string? TimeZone { get; init; }
}

public sealed record UpdateEquipmentRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool TracksState { get; init; }
    public bool IsSchedulable { get; init; }
    /// <summary>
    /// Runs jobs (D-022). Left out, a new row runs jobs when it is schedulable, and an update
    /// keeps the current value. A schedulable row that can't run jobs is a group, such as a department.
    /// </summary>
    public bool? CanRunJob { get; init; }
    public bool HoldsInventory { get; init; }

    /// <summary>Lots here mix into one balance per material (D-031). Left out, it keeps the current value.</summary>
    public bool? ComminglesLots { get; init; }
    public bool IsActive { get; init; } = true;
}
