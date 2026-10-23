using Pulaski.Domain.Execution;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Production by shift (D-044): each count lands in the shift running on its line, and the shift's production day.</summary>
public class ShiftProductionTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid Job = Guid.CreateVersion7();
    private static readonly Guid Kg = Guid.CreateVersion7();

    // Days 06:00–18:00 and nights 18:00–06:00, Monday to Friday, from Monday 19 October 2026; nights off on Friday.
    private static readonly ShiftSchedule TwoTwelves = new("2X12", "Two twelves", 7, new DateOnly(2026, 10, 19),
    [
        new("D", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(12), [0, 1, 2, 3, 4], "RED"),
        new("N", "Nights", new TimeOnly(18, 0), TimeSpan.FromHours(12), [0, 1, 2, 3]),
    ]);

    private static DateTimeOffset Local(int day, int hour, int minute = 0)
    {
        var local = new DateTime(2026, 10, day, hour, minute, 0);
        return new DateTimeOffset(local, Chicago.GetUtcOffset(local));
    }

    private static ProductionCount Made(DateTimeOffset at, decimal all) => new(Line, Job, Kg, at, IsDefective: false, all);

    private static ProductionCount Scrapped(DateTimeOffset at, decimal scrap) => new(Line, Job, Kg, at, IsDefective: true, scrap);

    private static IReadOnlyList<ShiftProductionTotal> Total(ShiftSchedule? schedule, DateOnly from, DateOnly to, params ProductionCount[] counts) =>
        ShiftProduction.Total(counts, _ => new LineCalendar(Chicago, schedule?.Code, schedule), from, to);

    [Fact]
    public void A_night_shift_counts_towards_the_day_it_started_even_after_midnight()
    {
        var totals = Total(TwoTwelves, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20),
            Made(Local(19, 7), 100), Scrapped(Local(19, 7), 10),
            Made(Local(19, 23), 200),
            Made(Local(20, 2), 300), Scrapped(Local(20, 2), 5),
            Made(Local(20, 8), 50));

        Assert.Equal(
            [(new DateOnly(2026, 10, 19), "D", 90m, 10m, 1), (new DateOnly(2026, 10, 19), "N", 495m, 5m, 2), (new DateOnly(2026, 10, 20), "D", 50m, 0m, 1)],
            totals.Select(t => (t.ProductionDate, t.Shift!.Code, t.GoodQty, t.ScrapQty, t.Reports)));
        Assert.Equal("RED", totals[0].Shift!.Crew);
        Assert.Equal("2X12", totals[0].ScheduleCode);
    }

    [Fact]
    public void The_first_days_early_hours_belong_to_the_night_before_and_are_left_out()
    {
        var totals = Total(TwoTwelves, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), Made(Local(20, 2), 300), Made(Local(20, 9), 40));

        Assert.Equal([40m], totals.Select(t => t.GoodQty));
    }

    [Fact]
    public void Counts_outside_shifts_or_on_lines_without_shifts_go_to_the_site_day()
    {
        // Friday night has no shift; a one-off Saturday shift (D-042) takes Saturday morning.
        var withSaturday = TwoTwelves.WithOverride(new DateOnly(2026, 10, 24), "D",
            new ShiftOverride(new DateOnly(2026, 10, 24), "D", true, new TimeOnly(6, 0), TimeSpan.FromHours(8), "Days"));
        var totals = Total(withSaturday, new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 24), Made(Local(23, 20), 10), Made(Local(24, 7), 20), Made(Local(24, 15), 30));

        Assert.Equal(
            [(new DateOnly(2026, 10, 23), (string?)null, (string?)null), (new DateOnly(2026, 10, 24), "D", "2X12"), (new DateOnly(2026, 10, 24), null, null)],
            totals.Select(t => (t.ProductionDate, t.Shift?.Code, t.ScheduleCode)));

        var noShifts = Total(null, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20), Made(Local(19, 23, 59), 1), Made(Local(20, 0, 1), 2));
        Assert.Equal([(new DateOnly(2026, 10, 19), 1m), (new DateOnly(2026, 10, 20), 2m)], noShifts.Select(t => (t.ProductionDate, t.GoodQty)));
    }

    [Fact]
    public void Every_count_lands_in_exactly_one_row()
    {
        var counts = Enumerable.Range(0, 96).Select(h => Made(Local(19, 0).AddMinutes(h * 37), h)).Concat([Scrapped(Local(21, 3), 7)]).ToArray();

        var totals = Total(TwoTwelves, new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 22), counts);

        Assert.Equal(counts.Where(c => !c.IsDefective).Sum(c => c.Quantity) - 7, totals.Sum(t => t.GoodQty));
        Assert.Equal(7, totals.Sum(t => t.ScrapQty));
        Assert.Equal(96, totals.Sum(t => t.Reports));
    }
}
