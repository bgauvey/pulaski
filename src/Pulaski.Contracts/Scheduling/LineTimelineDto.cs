namespace Pulaski.Contracts.Scheduling;

/// <summary>
/// One line's open jobs projected forward in time (D-014). Calculated on every read from the
/// planner's sequence, run standards, the changeover matrix, the shift calendar and live counts.
/// </summary>
public sealed record LineTimelineDto
{
    public required Guid LineId { get; init; }
    public required string LineCode { get; init; }
    public required string LineName { get; init; }

    /// <summary><c>auto</c> or <c>scheduler</c> (D-015).</summary>
    public required string ChangeoverMode { get; init; }

    public DateTimeOffset ProjectedAt { get; init; }

    /// <summary>False when no shift calendar covers the line and times assume it runs around the clock.</summary>
    public bool HasShiftCalendar { get; init; }

    public IReadOnlyList<TimelineSegmentDto> Segments { get; init; } = [];

    /// <summary>Warnings about the line as a whole; each segment carries its own.</summary>
    public IReadOnlyList<ScheduleWarningDto> Warnings { get; init; } = [];
}

/// <summary>
/// A span on the timeline: a job, or (<see cref="Kind"/> <c>changeover_placeholder</c>) a
/// changeover Pulaski inserted before the job named by <see cref="JobId"/>.
/// </summary>
public sealed record TimelineSegmentDto
{
    /// <summary><c>job</c> or <c>changeover_placeholder</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The job, or for a placeholder the production job it precedes.</summary>
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public string? WorkOrderNumber { get; init; }

    /// <summary>The job's material; for a placeholder, the material being changed to.</summary>
    public string? MaterialCode { get; init; }

    /// <summary>For a placeholder, the material being changed from.</summary>
    public string? FromMaterialCode { get; init; }

    /// <summary>Projected start; for a started job, when it actually started.</summary>
    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }

    /// <summary>Working time still ahead, excluding time outside shifts.</summary>
    public TimeSpan Work { get; init; }

    public decimal? PlannedQty { get; init; }
    public decimal? RemainingQty { get; init; }
    public string? UomCode { get; init; }

    /// <summary>The run-standard rate used, and whether it is <c>calculated</c> or <c>modified</c> (D-016).</summary>
    public decimal? RatePerHour { get; init; }
    public string? RateSource { get; init; }

    /// <summary>For a placeholder: <c>rule</c>, <c>standard</c> or <c>unknown</c>.</summary>
    public string? ChangeoverSource { get; init; }

    public DateTimeOffset? DueAt { get; init; }
    public DateTimeOffset? EarliestStart { get; init; }

    /// <summary>The published baseline (<c>job.planned_start</c> and <c>planned_end</c>), for adherence.</summary>
    public DateTimeOffset? BaselineStart { get; init; }
    public DateTimeOffset? BaselineEnd { get; init; }

    public bool IsLocked { get; init; }

    /// <summary>The job's concurrency token, for sequencing, pinning and dispatch; null for a placeholder.</summary>
    public long? Version { get; init; }

    public IReadOnlyList<ScheduleWarningDto> Warnings { get; init; } = [];
}

public sealed record ScheduleWarningDto(string Code, string Message);
