using System.Globalization;

namespace Pulaski.Domain.Shifts;

/// <summary>
/// One shift of a schedule (D-038): it starts at <see cref="Start"/> in the site's time zone, lasts
/// <see cref="Length"/>, and runs on the listed days of the cycle (0 is the cycle's first day). <see cref="Crew"/> is the
/// code of the crew that works it (D-040); a rotation is several shifts at the same time on different days.
/// </summary>
public sealed record ShiftDefinition(string Code, string Name, TimeOnly Start, TimeSpan Length, IReadOnlyList<int> Days, string? Crew = null);

/// <summary>A shift on the calendar: when it runs, the production day it counts towards (the day it starts on) and its crew.</summary>
public sealed record ShiftOccurrence(string Code, string Name, DateOnly ProductionDate, DateTimeOffset Start, DateTimeOffset End, string? Crew = null);

/// <summary>How equipment gets its working time (D-038).</summary>
public static class CalendarModes
{
    /// <summary>From its parent; at the top of the tree with nothing set, around the clock.</summary>
    public const string Inherit = "inherit";

    /// <summary>Its own shift schedule.</summary>
    public const string Schedule = "schedule";

    /// <summary>No shifts: it can work at any time, whatever its parent follows.</summary>
    public const string AroundTheClock = "around_the_clock";

    public static IReadOnlyList<string> All { get; } = [Inherit, Schedule, AroundTheClock];
}

/// <summary>
/// A site's shift schedule (D-038): shifts that repeat every <see cref="CycleDays"/> days from
/// <see cref="CycleStart"/>. Shift times aren't stored: they are worked out from the schedule when needed, so
/// a change applies at once and nothing has to be regenerated.
/// </summary>
public sealed record ShiftSchedule(string Code, string Name, int CycleDays, DateOnly CycleStart, IReadOnlyList<ShiftDefinition> Shifts)
{
    public const int MaxCycleDays = 56;
    public const int MaxCodeLength = 30;
    public static readonly TimeSpan MinLength = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(24);

    /// <summary>The cycle day a site-local date falls on, 0 to <see cref="CycleDays"/> − 1, also before the cycle start.</summary>
    public int CycleDay(DateOnly date)
    {
        var offset = (date.DayNumber - CycleStart.DayNumber) % CycleDays;
        return offset < 0 ? offset + CycleDays : offset;
    }

    /// <summary>Working time in one cycle.</summary>
    public TimeSpan HoursPerCycle => TimeSpan.FromTicks(Shifts.Sum(s => s.Length.Ticks * s.Days.Distinct().Count()));

    /// <summary>
    /// The shifts that overlap <c>[from, to)</c>, in order. Times are site-local wall-clock times: a shift starting in
    /// a daylight-saving gap starts when the clocks have gone forward, and its length is real elapsed time.
    /// </summary>
    public IReadOnlyList<ShiftOccurrence> Expand(TimeZoneInfo zone, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || Shifts.Count == 0)
        {
            return [];
        }

        // A shift that started the day before (or longer, for a 24-hour shift) can still be running at 'from'.
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime).AddDays(-2);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
        var result = new List<ShiftOccurrence>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var day = CycleDay(date);
            foreach (var shift in Shifts.Where(s => s.Days.Contains(day)))
            {
                var start = ToInstant(date.ToDateTime(shift.Start), zone);
                var end = start + shift.Length;
                if (end > from && start < to)
                {
                    result.Add(new ShiftOccurrence(shift.Code, shift.Name, date, start, end, shift.Crew));
                }
            }
        }

        return [.. result.OrderBy(o => o.Start)];
    }

    /// <summary>
    /// The shift that work starting at <paramref name="at"/> belongs to (D-040): the one running then, or the one starting
    /// within <paramref name="lead"/> (someone clocking on before the shift starts). Null between shifts.
    /// </summary>
    public ShiftOccurrence? ShiftAt(TimeZoneInfo zone, DateTimeOffset at, TimeSpan lead)
    {
        var near = Expand(zone, at, at + lead + TimeSpan.FromTicks(1));
        return near.FirstOrDefault(o => o.Start <= at && o.End > at) ?? near.FirstOrDefault(o => o.Start > at && o.Start <= at + lead);
    }

    /// <returns>What is wrong with the schedule, in words; empty when it can be saved.</returns>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (CycleDays is < 1 or > MaxCycleDays)
        {
            problems.Add($"The cycle is 1 to {MaxCycleDays} days.");
            return problems;
        }

        foreach (var duplicate in Shifts.GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"Two shifts are called {duplicate.Key}.");
        }

        foreach (var shift in Shifts)
        {
            var label = string.IsNullOrWhiteSpace(shift.Code) ? "A shift" : $"Shift {shift.Code}";
            if (string.IsNullOrWhiteSpace(shift.Code) || shift.Code.Length > 10)
            {
                problems.Add("Each shift needs a code of up to 10 characters.");
            }

            if (shift.Length < MinLength || shift.Length > MaxLength)
            {
                problems.Add($"{label} is 15 minutes to 24 hours long.");
            }

            if (shift.Days.Count == 0)
            {
                problems.Add($"{label} runs on no day; tick at least one.");
            }

            if (shift.Days.Any(d => d < 0 || d >= CycleDays))
            {
                problems.Add($"{label} names a day outside the {CycleDays}-day cycle.");
            }
        }

        if (problems.Count == 0 && FirstOverlap() is { } overlap)
        {
            problems.Add(overlap);
        }

        return problems;
    }

    // Checked on clock time, without daylight saving, over two cycles so a shift running past the end of the cycle
    // meets the next cycle's first.
    private string? FirstOverlap()
    {
        var start = CycleStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var occurrences = Expand(TimeZoneInfo.Utc, new DateTimeOffset(start), new DateTimeOffset(start.AddDays(2 * CycleDays + 1)));
        for (var i = 1; i < occurrences.Count; i++)
        {
            var (a, b) = (occurrences[i - 1], occurrences[i]);
            if (b.Start < a.End)
            {
                return $"Shift {b.Code} on day {CycleDay(b.ProductionDate) + 1} starts at {b.Start.ToString("HH:mm", CultureInfo.InvariantCulture)}, "
                    + $"before shift {a.Code} ends at {a.End.ToString("HH:mm", CultureInfo.InvariantCulture)}; shifts can't overlap.";
            }
        }

        return null;
    }

    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        // Clocks went forward past this time: start at the same clock time after the jump.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
