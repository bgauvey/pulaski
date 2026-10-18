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
        shifts.Count == 0 ? "No shifts" : string.Join(" · ", shifts.Select(s => $"{s.Code} {Times(s)}"));

    public static string Hours(decimal hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A length in hours as minutes, rounded to the minute; null when it isn't a positive number.</summary>
    public static int? Minutes(string? hours) =>
        decimal.TryParse(hours, NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h > 0 ? (int)Math.Round(h * 60) : null;
}
