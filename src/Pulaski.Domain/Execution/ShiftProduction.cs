using Pulaski.Domain.Shifts;

namespace Pulaski.Domain.Execution;

/// <summary>
/// One row of reported output (D-035): all of it (<see cref="IsDefective"/> false) or the scrapped part of it, on a line, for a
/// job, in the job's unit, at the moment it counts as made.
/// </summary>
public sealed record ProductionCount(Guid EquipmentId, Guid JobId, Guid UomId, DateTimeOffset OccurredAt, bool IsDefective, decimal Quantity);

/// <summary>Where a line's working time comes from (D-038): its site's time zone and the schedule it follows, if any.</summary>
public sealed record LineCalendar(TimeZoneInfo Zone, string? ScheduleCode, ShiftSchedule? Schedule);

/// <summary>Good and scrap a job made on a line in one shift (or, outside shifts, on one production day).</summary>
public sealed record ShiftProductionTotal(
    DateOnly ProductionDate,
    string? ScheduleCode,
    ShiftOccurrence? Shift,
    Guid EquipmentId,
    Guid JobId,
    Guid UomId,
    decimal GoodQty,
    decimal ScrapQty,
    int Reports);

/// <summary>
/// Production by shift (D-044). A count belongs to the shift running on its line when it occurred, from the schedule the line
/// follows with its one-off changes (D-038, D-042), and so to that shift's production day: the day it starts, also for a night
/// shift running past midnight. A count between shifts, or on a line without shifts, belongs to its site-local day with no shift.
/// Every count lands in exactly one row, so the rows add up to the runs' totals.
/// </summary>
public static class ShiftProduction
{
    public static (DateOnly ProductionDate, ShiftOccurrence? Shift) Attribute(DateTimeOffset at, LineCalendar calendar)
    {
        var shift = calendar.Schedule?.ShiftAt(calendar.Zone, at, TimeSpan.Zero);
        return (shift?.ProductionDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, calendar.Zone).DateTime), shift);
    }

    /// <summary>
    /// The counts by production day, shift, line, job and unit, for the production days <paramref name="from"/> to
    /// <paramref name="to"/>. Good is all output less the scrapped part.
    /// </summary>
    public static IReadOnlyList<ShiftProductionTotal> Total(
        IEnumerable<ProductionCount> counts, Func<Guid, LineCalendar> calendarOf, DateOnly from, DateOnly to) =>
        [.. counts
            .Select(c => (Count: c, Calendar: calendarOf(c.EquipmentId)))
            .Select(x => (x.Count, x.Calendar.ScheduleCode, Place: Attribute(x.Count.OccurredAt, x.Calendar)))
            .Where(x => x.Place.ProductionDate >= from && x.Place.ProductionDate <= to)
            .GroupBy(x => (x.Place.ProductionDate, ScheduleCode: x.Place.Shift is null ? null : x.ScheduleCode, x.Place.Shift?.Code, x.Place.Shift?.Start,
                x.Count.EquipmentId, x.Count.JobId, x.Count.UomId))
            .Select(g =>
            {
                var all = g.Where(x => !x.Count.IsDefective).Sum(x => x.Count.Quantity);
                var scrap = g.Where(x => x.Count.IsDefective).Sum(x => x.Count.Quantity);
                return new ShiftProductionTotal(
                    g.Key.ProductionDate, g.Key.ScheduleCode, g.First().Place.Shift, g.Key.EquipmentId, g.Key.JobId, g.Key.UomId,
                    all - scrap, scrap, g.Count(x => !x.Count.IsDefective));
            })
            .OrderBy(t => t.ProductionDate)
            .ThenBy(t => t.Shift?.Start ?? DateTimeOffset.MaxValue)
            .ThenBy(t => t.ScheduleCode, StringComparer.Ordinal)];
}
