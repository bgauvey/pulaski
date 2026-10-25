using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Overview;
using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services.Scheduling;

namespace Pulaski.Web.Services;

/// <summary>
/// The span a line row's strip covers on the Overview (D-070): the lines' current shifts, as the
/// API works it out; before that arrives, or with no line on a shift, six hours ago to eighteen
/// hours ahead on whole hours, so the "now" marker sits a quarter of the way in.
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

    public static OverviewWindow Of(OverviewDto overview) => new(overview.WindowStart, overview.WindowEnd);

    public TimeSpan Length => End - Start;

    /// <summary>Hours between ticks: every 2 for a shift-length window, else every 3.</summary>
    public int TickStep => Length <= TimeSpan.FromHours(12) ? 2 : 3;

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

/// <summary>A stretch of a line's actual time on the strip's state lane, positioned in percent.</summary>
public sealed record OverviewStateBar(OverviewStateSpanDto Span, double Left, double Width);

/// <summary>A quantity in one unit, e.g. good output against what was planned for the shift.</summary>
public sealed record OverviewQty(string UomCode, decimal Qty, decimal Of = 0m);

public sealed record OverviewOee(decimal? AvailabilityPct, decimal? PerformancePct, decimal? QualityPct, decimal? OeePct);

/// <summary>What one line row on the Overview shows from the schedule's projection.</summary>
public static class OverviewModel
{
    public static OeeShiftRowDto? CurrentShift(IEnumerable<OeeShiftRowDto> shifts, Guid equipmentId, DateTimeOffset now) =>
        shifts.FirstOrDefault(s => s.EquipmentId == equipmentId && s.Start <= now && now < s.End);

    public static OverviewOee? CurrentShiftOee(IEnumerable<OeeShiftRowDto> shifts, DateTimeOffset now)
    {
        var figures = shifts
            .Where(s => s.CanRunJob && s.Start <= now && now < s.End)
            .Select(s => s.Figures)
            .ToList();
        if (figures.Count == 0)
        {
            return null;
        }

        var plannedHours = figures.Sum(f => f.PlannedHoursForOee);
        var runHours = figures.Sum(f => f.RunHoursForAvailability);
        var ratedRunHours = figures.Sum(f => f.RatedRunHoursForPerformance);
        var idealHours = figures.Sum(f => f.IdealHours);
        var goodIdealHours = figures.Sum(f => f.GoodIdealHours);
        decimal? Ratio(decimal numerator, decimal denominator) => denominator > 0 ? numerator / denominator : null;

        var availabilityRatio = Ratio(runHours, plannedHours);
        var performanceRatio = Ratio(idealHours, ratedRunHours);
        var qualityRatio = Ratio(goodIdealHours, idealHours);
        decimal? availability = availabilityRatio is { } a ? Math.Round(a * 100m, 1) : null;
        decimal? performance = performanceRatio is { } p ? Math.Round(p * 100m, 1) : null;
        decimal? quality = qualityRatio is { } q ? Math.Round(q * 100m, 1) : null;
        var oee = availabilityRatio is { } ar && performanceRatio is { } pr && qualityRatio is { } qr
            ? Math.Round(ar * pr * qr * 100m, 1)
            : (decimal?)null;
        return new OverviewOee(availability, performance, quality, oee);
    }

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

    /// <summary>The state lane's stretches inside the window, cut to it.</summary>
    public static IReadOnlyList<OverviewStateBar> StateBars(IEnumerable<OverviewStateSpanDto> spans, OverviewWindow window) =>
        [.. spans
            .Where(s => s.End > window.Start && s.Start < window.End)
            .Select(s =>
            {
                var left = window.Percent(s.Start);
                return new OverviewStateBar(s, left, Math.Max(0.2d, window.Percent(s.End) - left));
            })];

    /// <summary>When a line's shift ends: its shift's end, else the next site-local midnight after it began.</summary>
    public static DateTimeOffset Until(OverviewLineDto line, string? timeZone) =>
        line.Shift?.End ?? (Display.FromSiteLocal(DateOnly.FromDateTime(Display.ToSite(line.From, timeZone).DateTime).AddDays(1).ToDateTime(TimeOnly.MinValue), timeZone) ?? line.From.AddDays(1));

    /// <summary>
    /// What the plan has a line make in <c>[from, to)</c>, by unit: each production job's planned quantity shared out over its
    /// projected time, so the part inside the span counts.
    /// </summary>
    public static IReadOnlyList<OverviewQty> Planned(IEnumerable<TimelineSegmentDto> segments, DateTimeOffset from, DateTimeOffset to) =>
        [.. segments
            .Where(s => s.Kind == "job" && s.JobType == "production" && s.PlannedQty is > 0 && s.UomCode is not null && s.End > s.Start)
            .Select(s =>
            {
                var start = s.Start > from ? s.Start : from;
                var end = s.End < to ? s.End : to;
                var share = end > start ? (decimal)((end - start).Ticks / (double)(s.End - s.Start).Ticks) : 0m;
                return (Uom: s.UomCode!, Qty: s.PlannedQty!.Value * share);
            })
            .Where(x => x.Qty > 0)
            .GroupBy(x => x.Uom)
            .Select(g => new OverviewQty(g.Key, g.Sum(x => x.Qty)))];

    /// <summary>Good output of the lines' shifts by unit, against what was planned in them (by unit, from <paramref name="planned"/>).</summary>
    public static IReadOnlyList<OverviewQty> Good(IEnumerable<OverviewLineDto> lines, IEnumerable<OverviewQty> planned)
    {
        var plan = planned.GroupBy(p => p.UomCode).ToDictionary(g => g.Key, g => g.Sum(p => p.Qty));
        var good = lines.SelectMany(l => l.Output).GroupBy(o => o.UomCode).ToDictionary(g => g.Key, g => g.Sum(o => o.GoodQty));
        return [.. good.Keys.Union(plan.Keys).Order(StringComparer.Ordinal).Select(u => new OverviewQty(u, good.GetValueOrDefault(u), plan.GetValueOrDefault(u)))];
    }

    /// <summary>Scrap of the lines' shifts by unit, against all output (good and scrap) in that unit.</summary>
    public static IReadOnlyList<OverviewQty> Scrap(IEnumerable<OverviewLineDto> lines) =>
        [.. lines.SelectMany(l => l.Output).GroupBy(o => o.UomCode).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OverviewQty(g.Key, g.Sum(o => o.ScrapQty), g.Sum(o => o.GoodQty + o.ScrapQty)))];

    /// <summary>The biggest reason for down time across the lines; null when nothing was down.</summary>
    public static OverviewReasonDto? TopReason(IEnumerable<OverviewLineDto> lines) =>
        lines.SelectMany(l => l.DownReasons)
            .GroupBy(r => (r.ReasonCode, r.Name, r.NoSignal))
            .Select(g => new OverviewReasonDto { ReasonCode = g.Key.ReasonCode, Name = g.Key.Name, NoSignal = g.Key.NoSignal, Minutes = g.Sum(r => r.Minutes) })
            .Where(r => r.Minutes > 0)
            .OrderByDescending(r => r.Minutes)
            .FirstOrDefault();

    /// <summary>Share of the job made so far, 0–1, from its planned and remaining quantity.</summary>
    public static double Made(TimelineSegmentDto job) =>
        job.PlannedQty is > 0 && job.RemainingQty is { } remaining
            ? (double)Math.Clamp((job.PlannedQty.Value - remaining) / job.PlannedQty.Value, 0m, 1m)
            : 0d;
}
