namespace Pulaski.DemoHistory;

/// <summary>
/// The two shifts the history fills, from the demo's three shifts a day (06:00, 14:00 and 22:00 site time): the previous one in full and the
/// current one up to now. The current shift's work is written for a whole shift and recorded up to now, so it holds what has happened so far;
/// its first two hours have one of each kind of record.
/// </summary>
public sealed record HistoryWindow(DateTimeOffset PreviousStart, DateTimeOffset CurrentStart, DateTimeOffset Now)
{
    /// <summary>The shifts' start times, in hours of the site-local day.</summary>
    public static readonly int[] ShiftStarts = [6, 14, 22];

    /// <summary>Records stop this long before now, so nothing is written in the future of a clock a little behind.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    public static HistoryWindow For(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var starts = Enumerable.Range(-2, 3)
            .SelectMany(d => ShiftStarts.Select(h => Local(today.AddDays(d), h, zone)))
            .Where(s => s <= now)
            .OrderBy(s => s)
            .ToList();
        return new HistoryWindow(starts[^2], starts[^1], now);
    }

    /// <summary>The previous shift's length (eight hours, but for a change of clocks).</summary>
    public TimeSpan PreviousLength => CurrentStart - PreviousStart;

    /// <summary>A moment of the previous shift, hours and minutes after it started.</summary>
    public DateTimeOffset Previous(int hours, int minutes = 0)
    {
        var at = PreviousStart + new TimeSpan(hours, minutes, 0);
        return at < CurrentStart ? at : CurrentStart - TimeSpan.FromMinutes(1);
    }

    /// <summary>A moment of the current shift, hours and minutes after it started; the history leaves out those after now.</summary>
    public DateTimeOffset Current(int hours, int minutes = 0) => CurrentStart + new TimeSpan(hours, minutes, 0);

    private static DateTimeOffset Local(DateOnly day, int hour, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0));
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
