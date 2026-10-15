using Pulaski.Contracts.Scheduling;

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

/// <summary>
/// A work order and where it stands: its open jobs as projected on their lines (or as windows on a
/// group, D-022), the slip against the published baseline and against the due date, and what has
/// been made so far. Read-only; every time comes from the projection at the moment of the read.
/// </summary>
public sealed record WorkOrderProgressDto
{
    public required WorkOrderDto Order { get; init; }

    /// <summary>Open jobs, earliest first.</summary>
    public IReadOnlyList<WorkOrderJobDto> Jobs { get; init; } = [];

    /// <summary>When the first open job is projected to start and the last to end; null without open jobs.</summary>
    public DateTimeOffset? ProjectedStart { get; init; }
    public DateTimeOffset? ProjectedEnd { get; init; }

    /// <summary>The published baseline of its jobs (D-018); null until the order is dispatched.</summary>
    public DateTimeOffset? BaselineStart { get; init; }
    public DateTimeOffset? BaselineEnd { get; init; }

    /// <summary>How much later than its baseline the order is projected to start; null when not later.</summary>
    public TimeSpan? StartSlip { get; init; }

    /// <summary>How much later than its due date the order is projected to finish; null when not later.</summary>
    public TimeSpan? FinishSlip { get; init; }

    /// <summary>Good quantity reported across all its jobs' runs.</summary>
    public decimal CompletedQty { get; init; }

    /// <summary>Quantity its open jobs plan to make; less than the order quantity when some is not on a line yet.</summary>
    public decimal PlacedQty { get; init; }

    public bool IsRunning { get; init; }

    /// <summary>Schedule warnings on its jobs, one per kind.</summary>
    public IReadOnlyList<ScheduleWarningDto> Warnings { get; init; } = [];

    /// <summary>One of its jobs overlaps other work on its line.</summary>
    public bool HasOverlap { get; init; }
}

/// <summary>One open job of a work order, as the board shows it. <see cref="Kind"/> is <c>job</c> or <c>window</c>.</summary>
public sealed record WorkOrderJobDto(
    Guid JobId,
    string JobNumber,
    string Kind,
    string Status,
    Guid EquipmentId,
    string EquipmentCode,
    DateTimeOffset Start,
    DateTimeOffset End,
    decimal? PlannedQty,
    decimal? RemainingQty,
    IReadOnlyList<ScheduleWarningDto> Warnings);
