using Pulaski.Application.Execution;
using Pulaski.Application.Labor;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Tests.Execution;

/// <summary>The production-by-shift report (D-044): rows by shift, and each job checked against its runs.</summary>
public class ProductionByShiftHandlerTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _line = Guid.CreateVersion7();
    private readonly Guid _kg = Guid.CreateVersion7();

    private static DateTimeOffset Local(int day, int hour)
    {
        var local = new DateTime(2026, 10, day, hour, 0, 0);
        return new DateTimeOffset(local, Chicago.GetUtcOffset(local));
    }

    private ProductionCountEntry Count(Guid job, string number, DateTimeOffset at, string kind, decimal qty) =>
        new(_line, "EXT-01", job, number, "WO-1", "PRF-1", "Profile", _kg, "kg", at, kind, qty);

    [Fact]
    public async Task Rows_come_by_shift_in_site_time_and_jobs_are_checked_against_their_runs()
    {
        var reports = _f.Stores.ProductionReports;
        reports.SiteTimeZones[_site] = "America/Chicago";
        var schedule = new ShiftSchedule("1X12", "Days", 7, new DateOnly(2026, 10, 19), [new("D", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(12), [0, 1, 2, 3, 4])]);
        reports.Calendars[_line] = new EquipmentShifts(_line, _site, "EXT-01", Chicago, Guid.CreateVersion7(), "1X12", schedule);

        var whole = Guid.CreateVersion7();
        var partly = Guid.CreateVersion7();
        reports.Counts.AddRange(
        [
            Count(whole, "WO-1-10", Local(19, 8), CountKind.Processed, 100), Count(whole, "WO-1-10", Local(19, 8), CountKind.Defective, 4),
            Count(whole, "WO-1-10", Local(20, 9), CountKind.Processed, 50),
            Count(partly, "WO-1-20", Local(20, 20), CountKind.Processed, 30),
        ]);
        reports.RunTotals[whole] = new JobRunTotal(whole, 146, 4);
        reports.RunTotals[partly] = new JobRunTotal(partly, 80, 0);

        var report = await new GetProductionByShiftHandler(reports)
            .HandleAsync(new GetProductionByShiftQuery(_site, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 19)), CancellationToken.None);

        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20), "America/Chicago"), (report.From, report.To, report.TimeZone));
        Assert.Equal(
            [(new DateOnly(2026, 10, 19), "D", "WO-1-10", 96m, 4m), (new DateOnly(2026, 10, 20), "D", "WO-1-10", 50m, 0m), (new DateOnly(2026, 10, 20), null, "WO-1-20", 30m, 0m)],
            report.Rows.Select(r => (r.ProductionDate, r.ShiftCode, r.JobNumber, r.GoodQty, r.ScrapQty)));
        Assert.Equal(6, report.Rows[0].ShiftStart!.Value.Hour);
        Assert.Equal(Chicago.GetUtcOffset(new DateTime(2026, 10, 19, 6, 0, 0)), report.Rows[0].ShiftStart!.Value.Offset);

        Assert.Equal([("WO-1-10", true), ("WO-1-20", false)], report.Jobs.Select(j => (j.JobNumber, j.MatchesRuns)));
        Assert.Equal((30m, 80m), (report.Jobs[1].GoodQty, report.Jobs[1].RunGoodQty));
    }
}
