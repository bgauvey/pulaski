namespace Pulaski.Contracts.EquipmentModel;

/// <summary>One node of the ISA-95 equipment hierarchy.</summary>
public sealed record EquipmentDto
{
    public required Guid Id { get; init; }
    public Guid? ParentId { get; init; }
    public Guid? SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Level { get; init; }
    public required string EquipmentRole { get; init; }
    public bool TracksState { get; init; }
    /// <summary>A planning target: jobs can be planned to it. Without <see cref="CanRunJob"/> it is a group, such as a department (D-022).</summary>
    public bool IsSchedulable { get; init; }

    /// <summary>Executes jobs, either planned to it directly or released to it from a group above it.</summary>
    public bool CanRunJob { get; init; }
    public bool HoldsInventory { get; init; }
    public string? TimeZone { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Distance from the enterprise root (0 = enterprise).</summary>
    public int Depth { get; init; }

    /// <summary>Concurrency token; send it back unchanged when updating.</summary>
    public long Version { get; init; }
}

/// <summary>An allowed ISA-95 (level, role) pair.</summary>
public sealed record EquipmentTypeDto(string Level, string Role);
