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

/// <summary>Moves a job before <see cref="BeforeJobId"/> on <see cref="LineId"/>, or to the end of that line.</summary>
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

public sealed record DispatchJobRequest
{
    public long Version { get; init; }
}

public sealed record PublishScheduleRequest
{
    /// <summary>How far ahead shift windows are loaded; the timeline query's default when null.</summary>
    public int? HorizonDays { get; init; }
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
