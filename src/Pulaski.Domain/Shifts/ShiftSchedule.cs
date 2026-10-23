using System.Globalization;

namespace Pulaski.Domain.Shifts;

/// <summary>
/// One shift of a schedule (D-038): it starts at <see cref="Start"/> in the site's time zone, lasts
/// <see cref="Length"/>, and runs on the listed days of the cycle (0 is the cycle's first day). <see cref="Crew"/> is the
/// code of the crew that works it (D-040); a rotation is several shifts at the same time on different days.
/// </summary>
public sealed record ShiftDefinition(string Code, string Name, TimeOnly Start, TimeSpan Length, IReadOnlyList<int> Days, string? Crew = null);

/// <summary>
/// A shift on the calendar: when it runs, the production day it counts towards (the day it starts on) and its crew.
/// <see cref="IsOverride"/> marks a shift that one date's override added or changed (D-042).
/// </summary>
public sealed record ShiftOccurrence(string Code, string Name, DateOnly ProductionDate, DateTimeOffset Start, DateTimeOffset End, string? Crew = null,
    bool IsOverride = false);

/// <summary>
/// One date's change to a schedule (D-042), leaving the repeating cycle alone. On <see cref="ProductionDate"/> the shift
/// <see cref="Code"/> runs at <see cref="Start"/> for <see cref="Length"/> instead of as the cycle says (or, when the cycle
/// has no such shift that day, runs as an extra one); when <see cref="IsWorking"/> is false it doesn't run that day.
/// </summary>
public sealed record ShiftOverride(DateOnly ProductionDate, string Code, bool IsWorking, TimeOnly Start, TimeSpan Length, string Name, string? Crew = null,
    string? Note = null);

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
/// a change applies at once and nothing has to be regenerated. <see cref="Overrides"/> change single dates (D-042).
/// </summary>
public sealed record ShiftSchedule(string Code, string Name, int CycleDays, DateOnly CycleStart, IReadOnlyList<ShiftDefinition> Shifts,
    IReadOnlyList<ShiftOverride>? Overrides = null)
{
    public const int MaxCycleDays = 56;
    public const int MaxOverrides = 1000;
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
        var overrides = Overrides ?? [];
        if (to <= from || (Shifts.Count == 0 && overrides.Count == 0))
        {
            return [];
        }

        // A shift that started the day before (or longer, for a 24-hour shift) can still be running at 'from'.
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime).AddDays(-2);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
        var byDate = overrides.Where(x => x.ProductionDate >= first && x.ProductionDate <= last).ToLookup(x => x.ProductionDate);
        var result = new List<ShiftOccurrence>();

        void Add(DateOnly date, string code, string name, TimeOnly at, TimeSpan length, string? crew, bool isOverride)
        {
            var start = ToInstant(date.ToDateTime(at), zone);
            var end = start + length;
            if (end > from && start < to)
            {
                result.Add(new ShiftOccurrence(code, name, date, start, end, crew, isOverride));
            }
        }

        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var day = CycleDay(date);
            var changed = byDate[date].ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
            foreach (var shift in Shifts.Where(s => s.Days.Contains(day) && !changed.ContainsKey(s.Code)))
            {
                Add(date, shift.Code, shift.Name, shift.Start, shift.Length, shift.Crew, isOverride: false);
            }

            // An override replaces the cycle's shift of its code that day, or adds one; a day off removes it.
            foreach (var entry in changed.Values.Where(x => x.IsWorking))
            {
                Add(date, entry.Code, entry.Name, entry.Start, entry.Length, entry.Crew, isOverride: true);
            }
        }

        return [.. result.OrderBy(o => o.Start)];
    }

    /// <summary>The schedule with one date's override for a shift set (added or replaced) or, with null, removed.</summary>
    public ShiftSchedule WithOverride(DateOnly date, string code, ShiftOverride? entry) => this with
    {
        Overrides = [.. (Overrides ?? []).Where(x => x.ProductionDate != date || !string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)),
            .. entry is null ? Array.Empty<ShiftOverride>() : [entry]],
    };

    /// <summary>The cycle's shift of this code on that date, ignoring overrides; null when the cycle has none that day.</summary>
    public ShiftDefinition? RegularShift(DateOnly date, string code)
    {
        var day = CycleDay(date);
        return Shifts.FirstOrDefault(s => s.Days.Contains(day) && string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
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

        problems.AddRange(OverrideProblems());

        if (problems.Count == 0 && FirstOverlap(this with { Overrides = [] }, CycleStart, CycleStart.AddDays(2 * CycleDays + 1), cycleDays: true) is { } overlap)
        {
            problems.Add(overlap);
        }

        // Each override's date, with the days either side a shift can run into.
        foreach (var date in (Overrides ?? []).Select(x => x.ProductionDate).Distinct().Order())
        {
            if (problems.Count == 0 && FirstOverlap(this, date.AddDays(-1), date.AddDays(2), cycleDays: false) is { } clash)
            {
                problems.Add(clash);
            }
        }

        return problems;
    }

    private IEnumerable<string> OverrideProblems()
    {
        var overrides = Overrides ?? [];
        if (overrides.Count > MaxOverrides)
        {
            yield return $"A schedule keeps at most {MaxOverrides} overrides.";
        }

        foreach (var duplicate in overrides.GroupBy(x => (x.ProductionDate, x.Code.ToUpperInvariant())).Where(g => g.Count() > 1))
        {
            yield return $"Shift {duplicate.Key.Item2} has two overrides on {Day(duplicate.Key.ProductionDate)}.";
        }

        foreach (var entry in overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.Code) || entry.Code.Length > 10)
            {
                yield return "Each override names a shift code of up to 10 characters.";
            }
            else if (entry.IsWorking && (entry.Length < MinLength || entry.Length > MaxLength))
            {
                yield return $"Shift {entry.Code} on {Day(entry.ProductionDate)} is 15 minutes to 24 hours long.";
            }
        }
    }

    // Checked on clock time, without daylight saving. For the cycle itself over two cycles, so a shift running past the
    // end of the cycle meets the next cycle's first; for an override over the days around it.
    private static string? FirstOverlap(ShiftSchedule schedule, DateOnly first, DateOnly last, bool cycleDays)
    {
        var start = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var occurrences = schedule.Expand(TimeZoneInfo.Utc, start, new DateTimeOffset(last.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        for (var i = 1; i < occurrences.Count; i++)
        {
            var (a, b) = (occurrences[i - 1], occurrences[i]);
            if (b.Start < a.End)
            {
                var day = cycleDays ? $"day {schedule.CycleDay(b.ProductionDate) + 1}" : Day(b.ProductionDate);
                return $"Shift {b.Code} on {day} starts at {b.Start.ToString("HH:mm", CultureInfo.InvariantCulture)}, "
                    + $"before shift {a.Code} ends at {a.End.ToString("HH:mm", CultureInfo.InvariantCulture)}; shifts can't overlap.";
            }
        }

        return null;
    }

    private static string Day(DateOnly date) => date.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

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
