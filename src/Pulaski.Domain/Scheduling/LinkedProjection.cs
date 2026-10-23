using Pulaski.Domain.Processes;

namespace Pulaski.Domain.Scheduling;

/// <summary>
/// One process link as it binds two jobs of a work order (D-023, D-046): <see cref="JobId"/>, on the link's later operation,
/// follows <see cref="FromJobId"/>, on its earlier one. A link with several jobs on its earlier operation has one row per job,
/// all with the same <see cref="LinkId"/>. <see cref="FromLabel"/> names the earlier operation ("10 Extrude").
/// </summary>
public sealed record JobLink(Guid LinkId, Guid JobId, Guid FromJobId, string DependencyType, TimeSpan Lag, string FromLabel);

/// <summary>When a job that is on no projected line starts and ends: a started or ended one's actual times, or a group job's window.</summary>
public sealed record KnownJobTimes(DateTimeOffset? Start, DateTimeOffset? End);

/// <summary>
/// Projects linked lines together (D-046), so a job waits for the jobs it follows on other lines as it does on the floor
/// (D-035): <c>after_start</c> until the first of them starts, <c>after_end</c> until the last of them ends, plus the lag.
/// It repeats the one-line projection (<see cref="TimelineProjector"/>), feeding each pass's times into the next as the jobs'
/// <see cref="PlannedJob.LinkReadyAt"/>, until nothing moves. Holds only ever move later, so it settles unless jobs wait on
/// each other in a circle (a job sequenced before the work it waits for); those are reported as
/// <see cref="ScheduleWarningCode.LinkCycle"/> after <see cref="MaxPasses"/>. It never reorders or moves jobs between lines,
/// and a firmed job keeps its time (<see cref="ScheduleWarningCode.LinkNotMet"/>).
/// </summary>
public static class LinkedProjection
{
    public const int MaxPasses = 16;

    public static IReadOnlyDictionary<Guid, LineTimeline> Project(
        IReadOnlyList<LinePlan> plans, IReadOnlyList<JobLink> links, IReadOnlyDictionary<Guid, KnownJobTimes> known)
    {
        var holds = new Dictionary<Guid, (DateTimeOffset At, string Label)>();
        var timelines = ProjectAll(plans, holds);
        if (links.Count == 0)
        {
            return timelines;
        }

        var unsettled = new HashSet<Guid>();
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            unsettled.Clear();
            foreach (var (jobId, hold) in Holds(links, Times(timelines, known)))
            {
                if (!holds.TryGetValue(jobId, out var current) || hold.At > current.At)
                {
                    holds[jobId] = hold;
                    unsettled.Add(jobId);
                }
            }

            if (unsettled.Count == 0)
            {
                return timelines;
            }

            timelines = ProjectAll(plans, holds);
        }

        return WithCycleWarnings(timelines, unsettled, holds);
    }

    /// <summary>When each linked job may start, and the operations that decide it.</summary>
    public static IReadOnlyDictionary<Guid, (DateTimeOffset At, string Label)> Holds(
        IReadOnlyList<JobLink> links, IReadOnlyDictionary<Guid, KnownJobTimes> times)
    {
        var holds = new Dictionary<Guid, (DateTimeOffset At, string Label)>();
        foreach (var link in links.GroupBy(l => (l.JobId, l.LinkId)))
        {
            var first = link.First();
            var from = link.Select(l => times.GetValueOrDefault(l.FromJobId)).ToList();
            DateTimeOffset? met = first.DependencyType == DependencyType.AfterStart
                ? from.Select(t => t?.Start).Where(s => s is not null).Min()
                : from.All(t => t?.End is not null) ? from.Max(t => t!.End) : null;
            if (met is not { } at)
            {
                continue;
            }

            var ready = at + first.Lag;
            var label = $"{first.FromLabel} {(first.DependencyType == DependencyType.AfterStart ? "starting" : "ending")}"
                + (first.Lag > TimeSpan.Zero ? $" plus {first.Lag}" : "");
            if (!holds.TryGetValue(link.Key.JobId, out var held) || ready > held.At)
            {
                holds[link.Key.JobId] = (ready, label);
            }
        }

        return holds;
    }

    private static Dictionary<Guid, LineTimeline> ProjectAll(IReadOnlyList<LinePlan> plans, Dictionary<Guid, (DateTimeOffset At, string Label)> holds) =>
        plans.ToDictionary(p => p.LineId, p => TimelineProjector.Project(p with
        {
            Jobs = [.. p.Jobs.Select(j => holds.TryGetValue(j.JobId, out var h) ? j with { LinkReadyAt = h.At, LinkLabel = h.Label } : j)],
        }));

    private static Dictionary<Guid, KnownJobTimes> Times(IReadOnlyDictionary<Guid, LineTimeline> timelines, IReadOnlyDictionary<Guid, KnownJobTimes> known)
    {
        var times = new Dictionary<Guid, KnownJobTimes>(known);
        foreach (var segment in timelines.Values.SelectMany(t => t.Segments).Where(s => s.Kind == SegmentKind.Job))
        {
            times[segment.JobId] = new KnownJobTimes(segment.Start, segment.End);
        }

        return times;
    }

    private static Dictionary<Guid, LineTimeline> WithCycleWarnings(
        IReadOnlyDictionary<Guid, LineTimeline> timelines, HashSet<Guid> unsettled, Dictionary<Guid, (DateTimeOffset At, string Label)> holds) =>
        timelines.ToDictionary(t => t.Key, t => t.Value with
        {
            Segments = [.. t.Value.Segments.Select(s => s.Kind == SegmentKind.Job && unsettled.Contains(s.JobId)
                ? s with
                {
                    Warnings = [.. s.Warnings, new ScheduleWarning(
                        ScheduleWarningCode.LinkCycle,
                        $"Can't be placed after {holds[s.JobId].Label}: that work waits, through the sequence, on this job. Resequence one of them.")],
                }
                : s)],
        });
}
