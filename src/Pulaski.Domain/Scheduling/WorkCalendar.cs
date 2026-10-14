namespace Pulaski.Domain.Scheduling;

/// <summary>A half-open span of time, <c>[Start, End)</c>.</summary>
public readonly record struct TimeWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>
/// Where a piece of work lands on the calendar. <c>BeyondCalendar</c> means part of it fell after
/// the last known window and was placed around the clock.
/// </summary>
public readonly record struct CalendarPlacement(DateTimeOffset Start, DateTimeOffset End, bool BeyondCalendar);

/// <summary>
/// The time a line can work, as sorted, disjoint windows: staffed shift windows minus calendar
/// exceptions, or, for a line with no shift calendar, around the clock minus exceptions. Work
/// placed on it accrues only inside windows. After the last shift window the calendar is unknown,
/// so work continues around the clock and is flagged.
/// </summary>
public sealed class WorkCalendar
{
    private static readonly TimeWindow Forever = new(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

    private readonly TimeWindow[] _windows;

    private WorkCalendar(TimeWindow[] windows, bool hasShiftCalendar)
    {
        _windows = windows;
        HasShiftCalendar = hasShiftCalendar;
    }

    /// <summary>No shifts and no exceptions: every instant is working time.</summary>
    public static WorkCalendar AlwaysOpen { get; } = AroundTheClock([]);

    /// <summary>False when the line has no shift calendar and is assumed to run around the clock.</summary>
    public bool HasShiftCalendar { get; }

    /// <summary>The working windows, sorted and disjoint.</summary>
    public IReadOnlyList<TimeWindow> Windows => _windows;

    /// <summary>Builds a calendar from shift windows (may overlap or touch) minus exception windows.</summary>
    public static WorkCalendar Create(IEnumerable<TimeWindow> shifts, IEnumerable<TimeWindow> exceptions) =>
        new(Subtract(Merge(shifts), Merge(exceptions)), hasShiftCalendar: true);

    /// <summary>A line with no shift calendar: open around the clock except for calendar exceptions.</summary>
    public static WorkCalendar AroundTheClock(IEnumerable<TimeWindow> exceptions) =>
        new(Subtract([Forever], Merge(exceptions)), hasShiftCalendar: false);

    /// <summary>
    /// Places <paramref name="work"/> of working time starting no earlier than <paramref name="from"/>.
    /// Zero work lands at the next working instant.
    /// </summary>
    public CalendarPlacement Place(DateTimeOffset from, TimeSpan work)
    {
        if (work < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(work), "Work cannot be negative.");
        }

        var remaining = work;
        DateTimeOffset? start = null;
        var cursor = from;

        for (var i = FirstWindowEndingAfter(from); i < _windows.Length; i++)
        {
            var window = _windows[i];
            var begin = cursor > window.Start ? cursor : window.Start;
            start ??= begin;

            var available = window.End - begin;
            if (remaining <= available)
            {
                return new CalendarPlacement(start.Value, begin + remaining, BeyondCalendar: false);
            }

            remaining -= available;
            cursor = window.End;
        }

        // Past the last window: carry on around the clock and say so.
        start ??= cursor;
        return new CalendarPlacement(start.Value, cursor + remaining, BeyondCalendar: true);
    }

    private int FirstWindowEndingAfter(DateTimeOffset instant)
    {
        int lo = 0, hi = _windows.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_windows[mid].End <= instant)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static List<TimeWindow> Merge(IEnumerable<TimeWindow> windows)
    {
        var result = new List<TimeWindow>();
        foreach (var w in windows.Where(w => w.End > w.Start).OrderBy(w => w.Start))
        {
            if (result.Count > 0 && w.Start <= result[^1].End)
            {
                var last = result[^1];
                result[^1] = last with { End = w.End > last.End ? w.End : last.End };
            }
            else
            {
                result.Add(w);
            }
        }

        return result;
    }

    private static TimeWindow[] Subtract(List<TimeWindow> windows, List<TimeWindow> blocked)
    {
        var result = new List<TimeWindow>(windows.Count);
        var b = 0;
        foreach (var window in windows)
        {
            var start = window.Start;
            while (b < blocked.Count && blocked[b].End <= start)
            {
                b++;
            }

            // Blocks are sorted and disjoint; walk those that overlap this window.
            for (var k = b; k < blocked.Count && blocked[k].Start < window.End; k++)
            {
                if (blocked[k].Start > start)
                {
                    result.Add(new TimeWindow(start, blocked[k].Start));
                }

                if (blocked[k].End > start)
                {
                    start = blocked[k].End;
                }
            }

            if (start < window.End)
            {
                result.Add(new TimeWindow(start, window.End));
            }
        }

        return [.. result];
    }
}
