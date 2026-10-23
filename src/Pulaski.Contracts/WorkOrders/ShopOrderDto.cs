using Pulaski.Contracts.Boms;

namespace Pulaski.Contracts.WorkOrders;

/// <summary>A printable work order with one traveler sheet for each job.</summary>
public sealed record ShopOrderDto
{
    public required WorkOrderDto Order { get; init; }
    public required string SiteTimeZone { get; init; }
    public IReadOnlyList<ShopOrderJobDto> Jobs { get; init; } = [];
}

/// <summary>The order, process operation, schedule, rate and planned components for one job.</summary>
public sealed record ShopOrderJobDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public bool IsGroup { get; init; }
    public bool WaitingForLineAssignment { get; init; }
    public IReadOnlyList<string> CandidateLines { get; init; } = [];
    public decimal? PlannedQty { get; init; }
    public string? UomCode { get; init; }
    public int? OperationNumber { get; init; }
    public string? OperationName { get; init; }
    public string? OperationDescription { get; init; }
    public DateTimeOffset? ScheduledStart { get; init; }
    public DateTimeOffset? ScheduledEnd { get; init; }
    public bool UsesProjectedSchedule { get; init; }
    public decimal? RatePerHour { get; init; }
    public string? RateUomCode { get; init; }
    public string? RateSource { get; init; }
    public required string BarcodeValue { get; init; }
    public IReadOnlyList<PlannedComponentDto> Components { get; init; } = [];
}
