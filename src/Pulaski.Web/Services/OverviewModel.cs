using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services.Scheduling;

namespace Pulaski.Web.Services;

/// <summary>
/// The span a line row's strip covers on the Overview: from six hours ago to eighteen hours
/// ahead, on whole hours, so the "now" marker sits a quarter of the way in. When shift
/// calendars exist this becomes the current shift (docs/admin-dashboard-brief.md).
/// </summary>
public sealed record OverviewWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public static readonly TimeSpan Behind = TimeSpan.FromHours(6);
    public static readonly TimeSpan Ahead = TimeSpan.FromHours(18);

    public static OverviewWindow Around(DateTimeOffset now)
    {
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        return new OverviewWindow(hour - Behind, hour + Ahead);
    }

    public TimeSpan Length => End - Start;

    /// <summary>Where <paramref name="at"/> falls, 0–100, clamped to the window.</summary>
    public double Percent(DateTimeOffset at) =>
        Math.Clamp((at - Start).TotalMilliseconds / Length.TotalMilliseconds * 100d, 0d, 100d);

    /// <summary>
    /// Every <paramref name="step"/> hours from the window start, in site-local wall time for the
    /// labels. The window's end is left out: 24 hours on, it would repeat the first label.
    /// </summary>
    public IReadOnlyList<(double Percent, string Label)> Ticks(string? timeZone, int step = 3)
    {
        var ticks = new List<(double, string)>();
        for (var at = Start; at < End; at = at.AddHours(step))
        {
            ticks.Add((Percent(at), ScheduleFormat.Time(at, timeZone)));
        }

        return ticks;
    }
}

/// <summary>A piece of a line's plan inside the window, positioned in percent.</summary>
public sealed record OverviewBar(TimelineSegmentDto Segment, double Left, double Width)
{
    public bool IsChangeover => Segment.Kind != "job" || Segment.JobType == "changeover";
}

/// <summary>What one line row on the Overview shows from the schedule's projection.</summary>
public static class OverviewModel
{
    /// <summary>The job the plan has on the line now, or else the next one to start; null when nothing is planned.</summary>
    public static TimelineSegmentDto? NowOrNext(IEnumerable<TimelineSegmentDto> segments, DateTimeOffset now)
    {
        var jobs = segments.Where(s => s.Kind == "job" && s.JobType == "production").ToList();
        return jobs.FirstOrDefault(s => s.Start <= now && now < s.End)
            ?? jobs.Where(s => s.Start > now).OrderBy(s => s.Start).FirstOrDefault();
    }

    /// <summary>The plan's segments that show in the window, cut to it; anything shorter than a sliver keeps a visible width.</summary>
    public static IReadOnlyList<OverviewBar> Bars(IEnumerable<TimelineSegmentDto> segments, OverviewWindow window) =>
        [.. segments
            .Where(s => s.End > window.Start && s.Start < window.End)
            .OrderBy(s => s.Start)
            .Select(s =>
            {
                var left = window.Percent(s.Start);
                return new OverviewBar(s, left, Math.Max(0.4d, window.Percent(s.End) - left));
            })];

    /// <summary>Share of the job made so far, 0–1, from its planned and remaining quantity.</summary>
    public static double Made(TimelineSegmentDto job) =>
        job.PlannedQty is > 0 && job.RemainingQty is { } remaining
            ? (double)Math.Clamp((job.PlannedQty.Value - remaining) / job.PlannedQty.Value, 0m, 1m)
            : 0d;
}
