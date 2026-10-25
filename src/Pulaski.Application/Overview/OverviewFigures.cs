using Pulaski.Contracts.Overview;
using Pulaski.Domain.MachineData;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Overview;

/// <summary>The time a line's Overview figures cover (D-070): its current shift so far, or the site-local day so far.</summary>
internal sealed record OverviewScope(DateTimeOffset From, ShiftOccurrence? Shift);

/// <summary>Down time with one reason, before the reason is named.</summary>
internal sealed record DownReason(Guid? ReasonCodeId, bool NoSignal, TimeSpan Length);

/// <summary>The Overview's figures from a line's history (D-070), pure so they can be checked without a database.</summary>
internal static class OverviewFigures
{
    public static readonly TimeSpan Behind = TimeSpan.FromHours(6);
    public static readonly TimeSpan Ahead = TimeSpan.FromHours(18);

    /// <summary>The shift the line is on at <paramref name="now"/> (no lead time: a shift about to start isn't current), else the site-local day.</summary>
    public static OverviewScope Scope(ShiftSchedule? schedule, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (schedule?.ShiftAt(zone, now, TimeSpan.Zero) is { } shift)
        {
            return new OverviewScope(shift.Start, shift);
        }

        return new OverviewScope(Utilization.DayStart(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime), zone), null);
    }

    /// <summary>
    /// The span the strips share: from the earliest current shift's start to the latest one's end; with no line on a shift, six
    /// hours back to eighteen ahead on whole hours, as before shifts existed.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End, bool IsShift) Window(IEnumerable<ShiftOccurrence> shifts, DateTimeOffset now)
    {
        var current = shifts.ToList();
        if (current.Count > 0)
        {
            return (current.Min(s => s.Start), current.Max(s => s.End), true);
        }

        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        return (hour - Behind, hour + Ahead, false);
    }

    /// <summary>Down time from <paramref name="from"/> on, by reason, most first. Micro-stops are run time and planned stops aren't down (D-052).</summary>
    public static IReadOnlyList<DownReason> Down(IEnumerable<UseSpan> uses, DateTimeOffset from) =>
        [.. Clip(uses, from)
            .Where(s => s.Use == TimeUse.Down)
            .GroupBy(s => (s.ReasonCodeId, NoSignal: s.ReasonCodeId is null && s.NoSignal))
            .Select(g => new DownReason(g.Key.ReasonCodeId, g.Key.NoSignal, TimeSpan.FromTicks(g.Sum(s => s.Length.Ticks))))
            .OrderByDescending(r => r.Length)];

    /// <summary>The stops overlapping <c>[from, now)</c> with no reason yet, each once; micro-stops left out.</summary>
    public static int Uncoded(IEnumerable<(Guid EquipmentId, StopSpan Stop)> stops, DateTimeOffset from, DateTimeOffset now) =>
        stops.Where(s => s.Stop.ReasonCodeId is null && !s.Stop.IsMicroStop && s.Stop.Start < now && (s.Stop.End ?? now) > from)
            .Select(s => (s.EquipmentId, s.Stop.Start))
            .Distinct()
            .Count();

    /// <summary>The line's time from <paramref name="from"/> on as the strip's lanes show it, neighbours of one use joined.</summary>
    public static IReadOnlyList<OverviewStateSpanDto> Lane(IEnumerable<UseSpan> uses, DateTimeOffset from)
    {
        var lane = new List<OverviewStateSpanDto>();
        foreach (var span in Clip(uses, from).OrderBy(s => s.Start))
        {
            var use = UseName(span);
            if (lane.Count > 0 && lane[^1].Use == use && lane[^1].End == span.Start)
            {
                lane[^1] = lane[^1] with { End = span.End };
            }
            else
            {
                lane.Add(new OverviewStateSpanDto { Start = span.Start, End = span.End, Use = use });
            }
        }

        return lane;
    }

    private static string UseName(UseSpan span) => span.Use switch
    {
        TimeUse.Run => "run",
        TimeUse.Down when span.NoSignal && span.ReasonCodeId is null => "no_signal",
        TimeUse.Down => "down",
        TimeUse.Planned => "planned",
        _ => "non_production",
    };

    private static IEnumerable<UseSpan> Clip(IEnumerable<UseSpan> uses, DateTimeOffset from) =>
        uses.Where(s => s.End > from).Select(s => s.Start < from ? s with { Start = from } : s);
}
