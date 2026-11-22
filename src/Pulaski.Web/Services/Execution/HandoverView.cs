using System.Globalization;
using Pulaski.Contracts.Handover;

namespace Pulaski.Web.Services.Execution;

/// <summary>How shift handover reads (#124). Pure.</summary>
public static class HandoverView
{
    /// <summary>"Day shift, Fri 20 Nov (06:00–14:00)", or "Fri 20 Nov" without shifts; times in the site's zone.</summary>
    public static string Shift(HandoverShiftDto shift, string? timeZone)
    {
        var day = shift.ProductionDate.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        if (shift.ShiftCode is null)
        {
            return day;
        }

        var name = shift.ShiftName ?? shift.ShiftCode;
        return shift is { Start: { } start, End: { } end }
            ? $"{name}, {day} ({Clock(start, timeZone)}–{Clock(end, timeZone)})"
            : $"{name}, {day}";
    }

    /// <summary>"Lee Ong at 14:05", or "Lee Ong, Thu 19 Nov 22:10" for another day.</summary>
    public static string Who(HandoverEntryDto entry, string? timeZone, DateTimeOffset now)
    {
        var at = Local(entry.WrittenAt, timeZone);
        var today = Local(now, timeZone).Date == at.Date;
        return $"{entry.WrittenByName ?? entry.WrittenBy}{(today ? " at " : ", ")}{at.ToString(today ? "HH:mm" : "ddd d MMM HH:mm", CultureInfo.InvariantCulture)}";
    }

    public static string OpenItems(int count) => count == 1 ? "1 open item" : $"{count} open items";

    private static string Clock(DateTimeOffset at, string? timeZone) => Local(at, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static DateTimeOffset Local(DateTimeOffset at, string? timeZone)
    {
        try
        {
            return timeZone is null ? at : TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        }
        catch (TimeZoneNotFoundException)
        {
            return at;
        }
    }
}
