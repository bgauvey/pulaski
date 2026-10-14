namespace Pulaski.Contracts.Scheduling;

/// <summary>
/// A job on a line. Production jobs need <see cref="WorkOrderId"/> and <see cref="PlannedQty"/>
/// and take the work order's material and unit; other types take a <see cref="PlannedDuration"/>.
/// The job goes before <see cref="BeforeJobId"/>, or at the end of the line when it is null.
/// </summary>
public sealed record CreateJobRequest
{
    public Guid LineId { get; init; }
    public string JobNumber { get; init; } = "";
    public string JobType { get; init; } = "";
    public Guid? WorkOrderId { get; init; }
    public decimal? PlannedQty { get; init; }
    public TimeSpan? PlannedDuration { get; init; }
    public bool CountsAsPlannedDowntime { get; init; }
    public DateTimeOffset? EarliestStart { get; init; }
    public Guid? BeforeJobId { get; init; }
}

/// <summary>
/// Moves a job before <see cref="BeforeJobId"/> on <see cref="LineId"/>, or to the end of that line.
/// A firmed job keeps its time, so its place on the new line follows from that time and
/// <see cref="BeforeJobId"/> is ignored.
/// </summary>
public sealed record SequenceJobRequest
{
    public long Version { get; init; }
    public Guid LineId { get; init; }
    public Guid? BeforeJobId { get; init; }
}

/// <summary>The job's pins (D-014): the lock, and the earliest time it may start.</summary>
public sealed record PinJobRequest
{
    public long Version { get; init; }
    public bool IsLocked { get; init; }
    public DateTimeOffset? EarliestStart { get; init; }
}

/// <summary>
/// Cancels a job. With <see cref="Replace"/>, a new job for the quantity it did not make takes
/// its slot (D-019); a job that has started can only be cancelled this way or outright.
/// </summary>
public sealed record CancelJobRequest
{
    public long Version { get; init; }
    public string? Reason { get; init; }
    public bool Replace { get; init; }
}

/// <summary>Dispatch, un-dispatch or firm a work order (D-018); the reason is recorded on the event.</summary>
public sealed record WorkOrderPlanningRequest
{
    public long Version { get; init; }
    public string? Reason { get; init; }
}

public sealed record ModifyRunStandardRequest
{
    public long Version { get; init; }
    public decimal RatePerHour { get; init; }
    public string? Note { get; init; }
}

public sealed record ClearRunStandardRequest
{
    public long Version { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Version 0 when the site still uses the default policy.</summary>
public sealed record SetSchedulingPolicyRequest
{
    public long Version { get; init; }
    public string ChangeoverMode { get; init; } = "";
}
