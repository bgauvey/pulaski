namespace Pulaski.Domain.Scheduling;

/// <summary>
/// A line under a group that could run a job (D-022): it can run jobs and has a capability for the
/// job's material. <c>Rate</c> is its run standard for that material, or null if it has none.
/// </summary>
public sealed record Runner(Guid LineId, string LineCode, RunRate? Rate);

/// <summary>How long the job would take on one runner, and whether that fits in the window.</summary>
public sealed record RunnerEstimate(Guid LineId, string LineCode, TimeSpan? Work, bool FitsWindow);

/// <summary>
/// A job waiting on a group, as a window: from its earliest start (or now) to its finish-by.
/// <see cref="Fastest"/> is the runner that needs the least working time.
/// </summary>
public sealed record GroupJobWindow(
    Guid JobId,
    DateTimeOffset Start,
    DateTimeOffset End,
    RunnerEstimate? Fastest,
    IReadOnlyList<RunnerEstimate> Runners,
    IReadOnlyList<ScheduleWarning> Warnings);

/// <summary>
/// Checks a group's jobs against their windows (D-022). Nothing is projected and no line is
/// picked: the floor decides where each job runs. Like the projector it warns and never blocks.
/// Each job is checked on its own; a group's jobs do not compete for its lines here, because
/// sharing capacity out between them would be finite-capacity scheduling (charter non-goal).
/// </summary>
public static class GroupWindows
{
    public static GroupJobWindow Evaluate(PlannedJob job, IReadOnlyList<Runner> runners, WorkCalendar calendar, DateTimeOffset now)
    {
        var start = job.EarliestStart ?? now;
        var end = job.LatestEnd ?? start;
        var remaining = Math.Max(0m, (job.PlannedQty ?? 0m) - job.GoodQty);
        var from = start > now ? start : now;

        var estimates = runners
            .Select(r =>
            {
                var work = Work(remaining, job.UomId, r.Rate);
                return new RunnerEstimate(r.LineId, r.LineCode, work, work is { } w && calendar.Place(from, w).End <= end);
            })
            .ToList();
        var fastest = estimates.Where(e => e.Work is not null).OrderBy(e => e.Work).ThenBy(e => e.LineCode, StringComparer.Ordinal).FirstOrDefault();

        var warnings = new List<ScheduleWarning>();
        if (end <= now)
        {
            warnings.Add(new ScheduleWarning(ScheduleWarningCode.WindowPassed, "The window has closed and no line has taken the job."));
        }
        else if (runners.Count == 0)
        {
            warnings.Add(new ScheduleWarning(ScheduleWarningCode.NoRunner, "No line in the group can run this material any more."));
        }
        else if (fastest is null)
        {
            warnings.Add(new ScheduleWarning(
                ScheduleWarningCode.NoRunStandard, "No line in the group has a run standard for this material; duration unknown."));
        }
        else if (!fastest.FitsWindow)
        {
            var finish = calendar.Place(from, fastest.Work!.Value).End;
            warnings.Add(new ScheduleWarning(
                ScheduleWarningCode.WindowTooShort,
                $"Even on {fastest.LineCode}, the fastest line, it would finish {finish - end} after the window closes."));
        }

        if (job.DueAt is { } due && end > due)
        {
            warnings.Add(new ScheduleWarning(ScheduleWarningCode.Late, $"The window closes {end - due} after the due date."));
        }

        return new GroupJobWindow(job.JobId, start, end, fastest, estimates, warnings);
    }

    /// <summary>Working time for the quantity at the rate, in whole seconds rounded up; null when the rate is unknown or in other units.</summary>
    private static TimeSpan? Work(decimal remaining, Guid? uomId, RunRate? rate)
    {
        if (rate is null || rate.RatePerHour <= 0 || (uomId is { } uom && uom != rate.UomId))
        {
            return null;
        }

        return TimeSpan.FromSeconds((double)decimal.Ceiling(remaining / rate.RatePerHour * 3600m));
    }
}
