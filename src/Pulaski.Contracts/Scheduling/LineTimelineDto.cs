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

    /// <summary>
    /// A group such as a department (D-022): its segments are windows (<c>window</c>), each with
    /// the lines that could run it, and nothing is projected.
    /// </summary>
    public bool IsGroup { get; init; }

    public DateTimeOffset ProjectedAt { get; init; }

    /// <summary>False when no shift calendar covers the line and times assume it runs around the clock.</summary>
    public bool HasShiftCalendar { get; init; }

    public IReadOnlyList<TimelineSegmentDto> Segments { get; init; } = [];

    /// <summary>Warnings about the line as a whole; each segment carries its own.</summary>
    public IReadOnlyList<ScheduleWarningDto> Warnings { get; init; } = [];

    /// <summary>Segments whose times intersect, e.g. work running into a firmed job.</summary>
    public IReadOnlyList<TimelineOverlapDto> Overlaps { get; init; } = [];
}

/// <summary>
/// Two segments on one line that intersect from <see cref="Start"/> to <see cref="End"/>. A segment
/// is named by its job and kind, because a changeover placeholder shares its job's id.
/// </summary>
public sealed record TimelineOverlapDto(
    Guid FirstJobId,
    string FirstKind,
    Guid SecondJobId,
    string SecondKind,
    DateTimeOffset Start,
    DateTimeOffset End);

/// <summary>
/// A span on the timeline: a job, or (<see cref="Kind"/> <c>changeover_placeholder</c>) a
/// changeover Pulaski inserted before the job named by <see cref="JobId"/>.
/// </summary>
public sealed record TimelineSegmentDto
{
    /// <summary><c>job</c>, <c>changeover_placeholder</c>, or on a group <c>window</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The job, or for a placeholder the production job it precedes.</summary>
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public Guid? WorkOrderId { get; init; }
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

    /// <summary>Finish by: the end of a group job's window, kept when a line takes it (D-022).</summary>
    public DateTimeOffset? LatestEnd { get; init; }

    /// <summary>The group the job was planned to, if any; the floor may move it between that group's lines.</summary>
    public Guid? GroupEquipmentId { get; init; }

    /// <summary>The process operation the job is for, when its order came from a process (D-024).</summary>
    public Guid? ProcessOperationId { get; init; }

    /// <summary>For a window: the lines under the group that could run it, with how long it would take on each.</summary>
    public IReadOnlyList<CandidateLineDto> Candidates { get; init; } = [];

    /// <summary>The published baseline (<c>job.planned_start</c> and <c>planned_end</c>), for adherence.</summary>
    public DateTimeOffset? BaselineStart { get; init; }
    public DateTimeOffset? BaselineEnd { get; init; }

    public bool IsLocked { get; init; }

    /// <summary>The job's concurrency token, for sequencing, pinning and dispatch; null for a placeholder.</summary>
    public long? Version { get; init; }

    public IReadOnlyList<ScheduleWarningDto> Warnings { get; init; } = [];
}

public sealed record ScheduleWarningDto(string Code, string Message);

/// <summary>A line that could run a group's job; <see cref="Work"/> is null without a run standard.</summary>
public sealed record CandidateLineDto(Guid LineId, string LineCode, TimeSpan? Work, bool FitsWindow);
