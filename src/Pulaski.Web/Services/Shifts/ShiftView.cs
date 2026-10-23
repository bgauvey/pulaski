using System.Globalization;
using Pulaski.Contracts.Shifts;

namespace Pulaski.Web.Services.Shifts;

/// <summary>How the Shifts screen labels days and shifts (D-038).</summary>
public static class ShiftView
{
    /// <summary>
    /// The heading of a cycle day: its weekday ("Mon") when the cycle is whole weeks, with the week ("W2 Mon") beyond
    /// one; "Day 3" otherwise, since the weekday then changes from one cycle to the next.
    /// </summary>
    public static string DayLabel(int day, int cycleDays, DateOnly cycleStart)
    {
        if (cycleDays % 7 != 0)
        {
            return $"Day {day + 1}";
        }

        var weekday = cycleStart.AddDays(day).ToString("ddd", CultureInfo.InvariantCulture);
        return cycleDays == 7 ? weekday : $"W{day / 7 + 1} {weekday}";
    }

    /// <summary>"06:00–14:00", with "+1" when it ends the next day.</summary>
    public static string Times(ShiftDto shift)
    {
        if (!TimeOnly.TryParseExact(shift.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            return shift.Start;
        }

        var endMinutes = start.Hour * 60 + start.Minute + shift.Minutes;
        var end = start.AddMinutes(shift.Minutes);
        return $"{start:HH\\:mm}–{end:HH\\:mm}{(endMinutes > 24 * 60 ? " +1" : "")}";
    }

    /// <summary>"A 06:00–14:00 · B 14:00–22:00".</summary>
    public static string Summary(IReadOnlyList<ShiftDto> shifts) =>
        shifts.Count == 0 ? "No shifts" : string.Join(" · ", shifts.Select(s => $"{s.Code} {Times(s)}{(s.Crew is { } crew ? $" ({crew})" : "")}"));

    public static string Hours(decimal hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A length in hours as minutes, rounded to the minute; null when it isn't a positive number.</summary>
    public static int? Minutes(string? hours) =>
        decimal.TryParse(hours, NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h > 0 ? (int)Math.Round(h * 60) : null;
}

/// <summary>How the Shifts screen describes one-off changes to a schedule (D-042).</summary>
public static class ShiftOverrideView
{
    /// <summary>The changes from <paramref name="today"/> on, or all of them, by day then shift.</summary>
    public static IReadOnlyList<ShiftOverrideDto> Upcoming(IReadOnlyList<ShiftOverrideDto> overrides, DateOnly today, bool all) =>
        [.. overrides.Where(o => all || o.Date >= today).OrderBy(o => o.Date).ThenBy(o => o.Code, StringComparer.Ordinal)];

    /// <summary>"Doesn't run", "Extra shift 06:00–14:00" or "06:00–14:00 instead of 14:00–22:00".</summary>
    public static string Describe(ShiftOverrideDto change, ShiftScheduleDto schedule)
    {
        if (!change.IsWorking)
        {
            return "Doesn't run";
        }

        var times = ShiftView.Times(new ShiftDto { Start = change.Start, Minutes = change.Minutes });
        return Regular(schedule, change.Date, change.Code) is { } regular
            ? $"{times} instead of {ShiftView.Times(regular)}"
            : $"Extra shift {times}";
    }

    /// <summary>What saving would do: change the cycle's shift that day, or add one.</summary>
    public static string Hint(ShiftScheduleDto schedule, DateOnly date, string code)
    {
        code = code.Trim();
        if (code.Length == 0)
        {
            return "Choose one of the schedule's shifts, or type a new code for an extra shift.";
        }

        return Regular(schedule, date, code) is { } regular
            ? $"{code} normally runs {ShiftView.Times(regular)} that day; this replaces it for that day only."
            : $"{code} doesn't normally run that day, so this adds it as an extra shift.";
    }

    /// <summary>The cycle's shift of this code on that date, or null when the cycle has none that day.</summary>
    public static ShiftDto? Regular(ShiftScheduleDto schedule, DateOnly date, string code)
    {
        var offset = (date.DayNumber - schedule.CycleStart.DayNumber) % schedule.CycleDays;
        var day = offset < 0 ? offset + schedule.CycleDays : offset;
        return schedule.Shifts.FirstOrDefault(s => s.Days.Contains(day) && string.Equals(s.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
