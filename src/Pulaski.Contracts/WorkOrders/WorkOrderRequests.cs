namespace Pulaski.Contracts.WorkOrders;

public sealed record CreateWorkOrderRequest
{
    public Guid SiteId { get; init; }
    public string OrderNumber { get; init; } = "";
    public Guid MaterialId { get; init; }
    public decimal PlannedQty { get; init; }
    public Guid UomId { get; init; }
    public Guid? TargetEquipmentId { get; init; }
    public DateTimeOffset? PlannedStart { get; init; }
    public DateTimeOffset? PlannedEnd { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int Priority { get; init; }
}

public sealed record ChangeWorkOrderStatusRequest
{
    public long Version { get; init; }
    public string Status { get; init; } = "";
    public string? Reason { get; init; }
}
