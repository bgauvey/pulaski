namespace Pulaski.Contracts.WorkOrders;

/// <summary>A work order (ISA-95 work request) with display fields joined in.</summary>
public sealed record WorkOrderDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string SiteCode { get; init; }
    public required string OrderNumber { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public decimal PlannedQty { get; init; }
    public required Guid UomId { get; init; }
    public required string UomCode { get; init; }
    public Guid? TargetEquipmentId { get; init; }
    public string? TargetEquipmentCode { get; init; }
    public DateTimeOffset? PlannedStart { get; init; }
    public DateTimeOffset? PlannedEnd { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int Priority { get; init; }
    public required string Status { get; init; }
    public required string Origin { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Concurrency token; send it back unchanged when changing status.</summary>
    public long Version { get; init; }

    /// <summary>Statuses this work order may move to next.</summary>
    public IReadOnlyList<string> NextStatuses { get; init; } = [];
}
