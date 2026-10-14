namespace Pulaski.Domain.Scheduling;

/// <summary>
/// One open job on the line, in planner sequence. <c>Material</c> is null for most
/// non-production jobs; <c>GoodQty</c> is reported across its runs so far; <c>StartedAt</c> is
/// when its first run started; <c>DueAt</c> is the work order due date. <c>FirmedStart</c> is
/// set for a firmed job: it is anchored there and does not move in time (D-019).
/// </summary>
public sealed record PlannedJob(
    Guid JobId,
    string JobType,
    string Status,
    MaterialRef? Material,
    decimal? PlannedQty,
    Guid? UomId,
    decimal GoodQty,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EarliestStart,
    TimeSpan? PlannedDuration,
    DateTimeOffset? DueAt,
    DateTimeOffset? FirmedStart = null);

/// <summary>The effective run-standard rate for one material on the line (D-016).</summary>
public sealed record RunRate(Guid MaterialId, Guid UomId, decimal RatePerHour, string Source);

/// <summary>
/// Everything the projector needs for one line. Jobs are in planner sequence;
/// <c>PreviousMaterial</c> is what the line last ran before them, for the first changeover.
/// </summary>
public sealed record LinePlan(
    Guid LineId,
    DateTimeOffset Now,
    string ChangeoverMode,
    MaterialRef? PreviousMaterial,
    IReadOnlyList<PlannedJob> Jobs,
    IReadOnlyList<RunRate> Rates,
    ChangeoverMatrix Changeovers,
    WorkCalendar Calendar);

public static class SegmentKind
{
    /// <summary>A job's own work.</summary>
    public const string Job = "job";

    /// <summary>A changeover the projector inserted in auto mode; it becomes a job at dispatch (D-015).</summary>
    public const string ChangeoverPlaceholder = "changeover_placeholder";
}

public static class ScheduleWarningCode
{
    public const string Late = "late";
    public const string NoRunStandard = "no_run_standard";
    public const string UomMismatch = "uom_mismatch";
    public const string MissingChangeover = "missing_changeover";
    public const string NoDuration = "no_duration";
    public const string BeyondCalendar = "beyond_calendar";
    public const string NoShiftCalendar = "no_shift_calendar";
    public const string FirmedConflict = "firmed_conflict";
}

public sealed record ScheduleWarning(string Code, string Message);

/// <summary>
/// A span on the line's timeline. For a placeholder, <see cref="JobId"/> is the production job it
/// precedes. <see cref="Start"/> of a started job is when it actually started.
/// </summary>
public sealed record TimelineSegment(
    string Kind,
    Guid JobId,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Work,
    decimal? RemainingQty,
    RunRate? Rate,
    MaterialRef? FromMaterial,
    ChangeoverResolution? Changeover,
    IReadOnlyList<ScheduleWarning> Warnings);

public sealed record LineTimeline(
    Guid LineId,
    DateTimeOffset ProjectedAt,
    IReadOnlyList<TimelineSegment> Segments,
    IReadOnlyList<ScheduleWarning> Warnings);

/// <summary>
/// Projects a line's job sequence forward in time (D-014). One pass, no I/O, deterministic: the
/// same plan always gives the same timeline. It never reorders jobs or moves them between lines;
/// it only works out when the planner's sequence will happen, and warns, never blocks. Firmed
/// jobs are fixed at their firmed start (D-019); the jobs after them continue from their end.
/// </summary>
public static class TimelineProjector
{
    public static LineTimeline Project(LinePlan plan)
    {
        var rates = plan.Rates.ToDictionary(r => r.MaterialId);
        var segments = new List<TimelineSegment>(plan.Jobs.Count * 2);
        var autoChangeovers = plan.ChangeoverMode == ChangeoverMode.Auto;

        // A running job is on the line now, whatever its rank says.
        var jobs = plan.Jobs.OrderBy(j => j.Status == JobStatus.Running ? 0 : 1).ToList();

        var cursor = plan.Now;
        var previous = plan.PreviousMaterial;
        var changeoverPlanned = false;   // a changeover job sits between the previous material and the next

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var started = job.Status == JobStatus.Running;
            var warnings = new List<ScheduleWarning>();

            if (job.JobType == JobType.Production && job.Material is { } material)
            {
                if (!started && previous is not null && !changeoverPlanned)
                {
                    var changeover = plan.Changeovers.Resolve(previous, material);
                    if (changeover.Duration > TimeSpan.Zero)
                    {
                        if (autoChangeovers)
                        {
                            var placed = plan.Calendar.Place(cursor, changeover.Duration);
                            segments.Add(new TimelineSegment(
                                SegmentKind.ChangeoverPlaceholder, job.JobId, placed.Start, placed.End, changeover.Duration,
                                null, null, previous, changeover, BeyondCalendarWarning(placed)));
                            cursor = placed.End;
                        }
                        else
                        {
                            warnings.Add(new ScheduleWarning(
                                ScheduleWarningCode.MissingChangeover,
                                $"The material changes here but no changeover job is planned ({changeover.Duration} expected)."));
                        }
                    }
                }

                var (work, remaining, rate) = ProductionWork(job, rates, warnings);
                segments.Add(PlaceJob(plan, job, ref cursor, work, remaining, rate, warnings, started));
                previous = material;
                changeoverPlanned = false;
            }
            else if (job.JobType == JobType.Changeover)
            {
                var duration = job.PlannedDuration ?? ChangeoverJobDuration(plan, previous, jobs, i);
                if (duration is null)
                {
                    warnings.Add(new ScheduleWarning(
                        ScheduleWarningCode.NoDuration, "No planned duration, and no material change to size it from."));
                }

                segments.Add(PlaceJob(plan, job, ref cursor, Remaining(job, duration ?? TimeSpan.Zero, plan.Now), null, null, warnings, started));
                changeoverPlanned = true;
            }
            else
            {
                if (job.PlannedDuration is null)
                {
                    warnings.Add(new ScheduleWarning(ScheduleWarningCode.NoDuration, "No planned duration."));
                }

                segments.Add(PlaceJob(
                    plan, job, ref cursor, Remaining(job, job.PlannedDuration ?? TimeSpan.Zero, plan.Now), null, null, warnings, started));
            }
        }

        IReadOnlyList<ScheduleWarning> lineWarnings = !plan.Calendar.HasShiftCalendar
            ? [new ScheduleWarning(ScheduleWarningCode.NoShiftCalendar, "No shift calendar covers this line; times assume it runs around the clock.")]
            : [];

        return new LineTimeline(plan.LineId, plan.Now, segments, lineWarnings);
    }

    private static TimelineSegment PlaceJob(
        LinePlan plan,
        PlannedJob job,
        ref DateTimeOffset cursor,
        TimeSpan work,
        decimal? remaining,
        RunRate? rate,
        List<ScheduleWarning> warnings,
        bool started)
    {
        var from = cursor;
        if (!started && job.FirmedStart is { } firmed)
        {
            // Anchored: the job starts at its firmed time (or now, if that has passed), whatever
            // runs before it. Work that runs into the anchor is flagged, never moved past it.
            if (cursor > firmed)
            {
                warnings.Add(new ScheduleWarning(
                    ScheduleWarningCode.FirmedConflict,
                    $"Firmed to start at {firmed:u}, but the line is busy or the time has passed until {cursor:u}."));
            }

            from = firmed > plan.Now ? firmed : plan.Now;
        }
        else if (!started && job.EarliestStart is { } earliest && earliest > from)
        {
            from = earliest;
        }

        var placed = plan.Calendar.Place(from, work);
        cursor = placed.End;
        warnings.AddRange(BeyondCalendarWarning(placed));

        if (job.DueAt is { } due && placed.End > due)
        {
            warnings.Add(new ScheduleWarning(ScheduleWarningCode.Late, $"Projected to finish {placed.End - due} after the due date."));
        }

        var start = started && job.StartedAt is { } actual ? actual : placed.Start;
        return new TimelineSegment(SegmentKind.Job, job.JobId, start, placed.End, work, remaining, rate, null, null, warnings);
    }

    private static (TimeSpan Work, decimal? Remaining, RunRate? Rate) ProductionWork(
        PlannedJob job, Dictionary<Guid, RunRate> rates, List<ScheduleWarning> warnings)
    {
        var remaining = Math.Max(0m, (job.PlannedQty ?? 0m) - job.GoodQty);

        if (!rates.TryGetValue(job.Material!.Id, out var rate))
        {
            warnings.Add(new ScheduleWarning(
                ScheduleWarningCode.NoRunStandard, "No run standard for this material on this line; duration unknown."));
            return (TimeSpan.Zero, remaining, null);
        }

        if (job.UomId is { } uom && uom != rate.UomId)
        {
            warnings.Add(new ScheduleWarning(
                ScheduleWarningCode.UomMismatch, "The job and the run standard use different units; duration unknown."));
            return (TimeSpan.Zero, remaining, rate);
        }

        // Whole seconds, rounded up, computed in decimal so large quantities stay exact.
        var seconds = decimal.Ceiling(remaining / rate.RatePerHour * 3600m);
        return (TimeSpan.FromSeconds((double)seconds), remaining, rate);
    }

    /// <summary>A changeover job without its own duration is sized from the material change it covers.</summary>
    private static TimeSpan? ChangeoverJobDuration(LinePlan plan, MaterialRef? previous, List<PlannedJob> jobs, int index)
    {
        var next = jobs.Skip(index + 1).FirstOrDefault(j => j.JobType == JobType.Production)?.Material;
        return previous is null || next is null ? null : plan.Changeovers.Resolve(previous, next).Duration;
    }

    /// <summary>Work left on a fixed-duration job; a running one has used the wall-clock time since it started.</summary>
    private static TimeSpan Remaining(PlannedJob job, TimeSpan duration, DateTimeOffset now)
    {
        if (job.Status != JobStatus.Running || job.StartedAt is not { } started)
        {
            return duration;
        }

        var left = duration - (now - started);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static IReadOnlyList<ScheduleWarning> BeyondCalendarWarning(CalendarPlacement placed) =>
        placed.BeyondCalendar
            ? [new ScheduleWarning(ScheduleWarningCode.BeyondCalendar, "Runs past the last generated shift; placed around the clock.")]
            : [];
}
