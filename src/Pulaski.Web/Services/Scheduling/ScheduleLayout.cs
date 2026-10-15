using System.Globalization;

namespace Pulaski.Web.Services.Scheduling;

/// <summary>Maps a time window onto 0–100 % of a Gantt track, in the site's time zone.</summary>
public sealed record GanttScale(DateTimeOffset Start, TimeSpan Span, string? TimeZone)
{
    public DateTimeOffset End => Start + Span;

    /// <summary>Position of <paramref name="at"/> as a percentage of the track; below 0 or above 100 outside it.</summary>
    public double RawPercent(DateTimeOffset at) => (at - Start).TotalMilliseconds / Span.TotalMilliseconds * 100d;

    /// <summary>Position of <paramref name="at"/> as a percentage of the track, clamped to the track.</summary>
    public double Percent(DateTimeOffset at) =>
        Math.Clamp((at - Start).TotalMilliseconds / Span.TotalMilliseconds * 100d, 0d, 100d);

    /// <summary>Width of a span as a percentage of the track, after clamping both ends.</summary>
    public double Width(DateTimeOffset from, DateTimeOffset to) => Math.Max(0d, Percent(to) - Percent(from));

    public bool IsVisible(DateTimeOffset from, DateTimeOffset to) => to > Start && from < End;

    /// <summary>The instant at a fraction (0–1) of the track.</summary>
    public DateTimeOffset At(double fraction) => Start + Span * Math.Clamp(fraction, 0d, 1d);

    /// <summary>Gridlines at a step that keeps 6–24 of them on the track, aligned to site-local wall time.</summary>
    public IReadOnlyList<GanttTick> Ticks()
    {
        var step = Span.TotalHours switch
        {
            <= 24 => TimeSpan.FromHours(2),
            <= 72 => TimeSpan.FromHours(6),
            <= 24 * 7 => TimeSpan.FromHours(12),
            _ => TimeSpan.FromDays(1),
        };

        var local = Display.ToSite(Start, TimeZone).DateTime.Date;
        var ticks = new List<GanttTick>();
        for (var wall = local; ; wall += step)
        {
            var at = Display.FromSiteLocal(wall, TimeZone)!.Value;
            if (at >= End)
            {
                break;
            }

            if (at >= Start)
            {
                var isDay = wall.TimeOfDay == TimeSpan.Zero;
                var label = isDay
                    ? wall.ToString("ddd d MMM", CultureInfo.InvariantCulture)
                    : wall.ToString("HH:mm", CultureInfo.InvariantCulture);
                ticks.Add(new GanttTick(at, Percent(at), label, isDay));
            }
        }

        return ticks;
    }
}

public sealed record GanttTick(DateTimeOffset At, double Percent, string Label, bool IsDayStart);

public static class ScheduleLayout
{
    /// <summary>
    /// Packs a line's segments into lanes so overlapping bars stack instead of hiding each other.
    /// Each segment takes the first lane whose last bar has ended. Returns the lane per key.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Lanes(IEnumerable<BoardItem> items)
    {
        var laneEnds = new List<DateTimeOffset>();
        var lanes = new Dictionary<string, int>();
        foreach (var item in items.OrderBy(i => i.Segment.Start).ThenBy(i => i.Segment.End))
        {
            var lane = laneEnds.FindIndex(end => end <= item.Segment.Start);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(item.Segment.End);
            }
            else
            {
                laneEnds[lane] = item.Segment.End;
            }

            lanes[item.Key] = lane;
        }

        return lanes;
    }

    /// <summary>
    /// Where a job dropped at <paramref name="at"/> on a line goes in its sequence (D-017): before
    /// the first job whose midpoint is later than the drop, or at the end (null). The running job,
    /// placeholders and the job being moved are skipped, since nothing can be put before them.
    /// </summary>
    public static Guid? BeforeJobAt(IEnumerable<BoardItem> lineItems, DateTimeOffset at, Guid? movingJobId)
    {
        var next = lineItems
            .Where(i => !i.IsPlaceholder && i.JobId != movingJobId && i.Segment.Status != "running")
            .OrderBy(i => i.Segment.Start)
            .FirstOrDefault(i => i.Segment.Start + (i.Segment.End - i.Segment.Start) / 2 > at);
        return next?.JobId;
    }

    /// <summary>The job that follows <paramref name="jobId"/> on its line, or null when it is last.</summary>
    public static Guid? NextJobAfter(IEnumerable<BoardItem> lineItems, Guid jobId)
    {
        var jobs = lineItems.Where(i => !i.IsPlaceholder).OrderBy(i => i.Segment.Start).ToList();
        var index = jobs.FindIndex(i => i.JobId == jobId);
        return index >= 0 && index + 1 < jobs.Count ? jobs[index + 1].JobId : null;
    }

    /// <summary>The six-week grid (Monday first) that shows <paramref name="month"/>.</summary>
    public static IReadOnlyList<DateOnly> MonthGrid(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        return [.. Enumerable.Range(0, 42).Select(start.AddDays)];
    }

    /// <summary>The site-local day as an instant range [start, end).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly day, string? timeZone) =>
        (Display.FromSiteLocal(day.ToDateTime(TimeOnly.MinValue), timeZone)!.Value,
         Display.FromSiteLocal(day.AddDays(1).ToDateTime(TimeOnly.MinValue), timeZone)!.Value);

    /// <summary>Items that run during any part of the site-local day, in start order.</summary>
    public static IReadOnlyList<BoardItem> OnDay(IEnumerable<BoardItem> items, DateOnly day, string? timeZone)
    {
        var (start, end) = DayRange(day, timeZone);
        return [.. items
            .Where(i => i.Segment.Start < end && (i.Segment.End > start || (i.Segment.End == i.Segment.Start && i.Segment.Start >= start)))
            .OrderBy(i => i.Segment.Start)];
    }

    public static DateOnly SiteDate(DateTimeOffset at, string? timeZone) => DateOnly.FromDateTime(Display.ToSite(at, timeZone).DateTime);
}

public static class ScheduleFormat
{
    /// <summary>'2h 15m', '45m', '3d 4h'.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = span.Negate();
        }

        if (span.TotalDays >= 1)
        {
            return span.Hours == 0 ? $"{(int)span.TotalDays}d" : $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    }

    public static string Qty(decimal? qty, string? uom) =>
        qty is null ? "" : $"{qty.Value.ToString("#,##0.###", CultureInfo.InvariantCulture)} {uom}".TrimEnd();

    /// <summary>A short site-local time: 'Wed 14 Oct 06:30'.</summary>
    public static string When(DateTimeOffset? at, string? timeZone) =>
        at is null ? "" : Display.ToSite(at.Value, timeZone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset at, string? timeZone) =>
        Display.ToSite(at, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
}
