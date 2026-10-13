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
    public bool HoldsInventory { get; init; }
    public string? TimeZone { get; init; }
}

public sealed record UpdateEquipmentRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool TracksState { get; init; }
    public bool IsSchedulable { get; init; }
    public bool HoldsInventory { get; init; }
    public bool IsActive { get; init; } = true;
}
