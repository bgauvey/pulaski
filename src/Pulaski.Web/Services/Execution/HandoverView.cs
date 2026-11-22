using System.Globalization;
using Pulaski.Contracts.Handover;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Execution;

/// <summary>How shift handover reads (#124). Pure.</summary>
public static class HandoverView
{
    /// <summary>"Day shift, Fri 20 Nov (06:00–14:00)", or "Fri 20 Nov" without shifts; times in the site's zone.</summary>
    public static string Shift(HandoverShiftDto shift, string? timeZone)
    {
        var day = shift.ProductionDate.ToString("ddd d MMM", CultureInfo.CurrentCulture);
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
    public static string Who(Text l, HandoverEntryDto entry, string? timeZone, DateTimeOffset now)
    {
        var at = Local(entry.WrittenAt, timeZone);
        var who = entry.WrittenByName ?? entry.WrittenBy;
        return Local(now, timeZone).Date == at.Date
            ? l["{0} at {1}", who, at.ToString("HH:mm", CultureInfo.InvariantCulture)]
            : $"{who}, {at.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture)}";
    }

    public static string OpenItems(Text l, int count) => count == 1 ? l["1 open item"] : l["Open items: {0}", count];

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
