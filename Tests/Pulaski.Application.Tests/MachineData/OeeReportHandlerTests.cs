using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Application.MachineData;
using Pulaski.Domain.MachineData;
using Pulaski.Domain.Shifts;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.MachineData;

/// <summary>
/// The utilization and OEE report (D-043). The fixture's clock is Monday 2026-10-12 14:00 UTC, the end of the line's day shift:
/// a series line of two machines, one jammed for an hour, the other in a planned changeover for half an hour.
/// </summary>
public class OeeReportHandlerTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _line = Guid.CreateVersion7();
    private readonly Guid _filler = Guid.CreateVersion7();
    private readonly Guid _capper = Guid.CreateVersion7();
    private readonly Guid _material = Guid.CreateVersion7();
    private readonly Guid _run = Guid.CreateVersion7();
    private readonly Guid _jam = Guid.CreateVersion7();
    private readonly Guid _changeover = Guid.CreateVersion7();
    private readonly Guid _kg;

    public OeeReportHandlerTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m).Id;
        _f.Stores.Units.AddMaterial(_material);
        var schedule = new ShiftSchedule("DAYS", "Days", 7, Day, [new("A", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4, 5, 6], "RED")]);
        EquipmentShifts Days(Guid id, string code) => new(id, _site, code, TimeZoneInfo.Utc, Guid.CreateVersion7(), "DAYS", schedule);

        Oee.AddEquipment(new(_line, "L1", "Pack line", null, StateDerivations.Series, null, CanRunJob: true), Days(_line, "L1"));
        Oee.AddEquipment(new(_filler, "FIL", "Filler", _line, StateDerivations.OwnTags, null, CanRunJob: false), Days(_filler, "FIL"));
        Oee.AddEquipment(new(_capper, "CAP", "Capper", _line, StateDerivations.OwnTags, null, CanRunJob: false), Days(_capper, "CAP"));
        Oee.AddReason(new(_jam, "JAM", "Jam", UtilizationClasses.Downtime));
        Oee.AddReason(new(_changeover, "CHG", "Changeover", UtilizationClasses.Planned));

        // Filler: executing from 05:00, jammed 08:00–09:00.
        Oee.AddState(_filler, new(At(5), 6, 1));
        Oee.AddState(_filler, new(At(8), 2, 1));
        Oee.AddState(_filler, new(At(9), 6, 1));
        Oee.AddStop(_filler, new(At(8), At(9), _jam, UtilizationClasses.Downtime, false));

        // Capper: a changeover 10:00–10:30 and a two-minute micro-stop at noon.
        Oee.AddState(_capper, new(At(5), 6, 1));
        Oee.AddState(_capper, new(At(10), 2, 1));
        Oee.AddState(_capper, new(At(10.5), 6, 1));
        Oee.AddState(_capper, new(At(12), 5, 1));
        Oee.AddState(_capper, new(At(12) + TimeSpan.FromMinutes(2), 6, 1));
        Oee.AddStop(_capper, new(At(10), At(10.5), _changeover, UtilizationClasses.Planned, false));
        Oee.AddStop(_capper, new(At(12), At(12) + TimeSpan.FromMinutes(2), null, null, true));

        // The line's job ran all shift at an ideal 100 kg/h: 530 kg made, 30 of it scrap.
        Oee.AddRun(new(_run, _line, At(6), null, _material, "M-1", "Widget", _kg, 100m, _kg, "J-1"));
        Oee.AddCount(_line, new(_run, At(8), _kg, 200m, 0m));
        Oee.AddCount(_line, new(_run, At(13), _kg, 330m, 30m));
    }

    private InMemoryOee Oee => _f.Stores.Oee;

    private static DateTimeOffset At(double hours) => T0.AddHours(hours);

    private GetOeeReportHandler Handler() => new(Oee, _f.Stores.Units, _f.Time);

    [Fact]
    public async Task A_line_takes_its_time_from_its_machines_and_its_output_from_its_runs()
    {
        var report = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, null), CancellationToken.None);

        Assert.Equal(["L1", "FIL", "CAP"], report.Equipment.Select(e => e.Code));
        Assert.Equal([0, 1, 1], report.Equipment.Select(e => e.Depth));

        var line = report.Equipment[0].Figures;
        Assert.Equal((8m, 0.5m, 7.5m, 6.5m, 1m, 0m), (line.ScheduledHours, line.PlannedStopHours, line.PlannedHours, line.RunHours, line.DownHours, line.UncodedHours));
        Assert.Equal(2, line.Stops);
        Assert.Equal((86.7m, 81.5m, 94.3m, 66.7m), (line.AvailabilityPct, line.PerformancePct, line.QualityPct, line.OeePct));

        // A machine below the line has no runs of its own: availability only.
        var filler = report.Equipment[1].Figures;
        Assert.Equal((7m, 1m, 87.5m, (decimal?)null, (decimal?)null), (filler.RunHours, filler.DownHours, filler.AvailabilityPct, filler.PerformancePct, filler.OeePct));
        var capper = report.Equipment[2].Figures;
        Assert.Equal((100m, 1, 1), (capper.AvailabilityPct, capper.Stops, capper.MicroStops));

        var shift = Assert.Single(report.Shifts, s => s.EquipmentId == _line);
        Assert.Equal(("A", "Days", "RED", Day, 66.7m), (shift.ShiftCode, shift.ShiftName, shift.CrewCode, shift.ProductionDate, shift.Figures.OeePct));

        var product = Assert.Single(report.Products);
        Assert.Equal(("M-1", "kg", 500m, 30m, 1), (product.MaterialCode, product.UomCode, product.GoodQty, product.ScrapQty, product.Runs));
        Assert.Equal(["L1"], product.EquipmentCodes);
        Assert.Equal(66.7m, product.Figures.OeePct);

        Assert.Equal(
            [("run", null, 6.5m), ("down", "JAM", 1m), ("planned", "CHG", 0.5m)],
            report.Utilization.Where(u => u.EquipmentId == _line).Select(u => (u.Use, u.ReasonCode, u.Hours)));
        Assert.Equal(12.5m, report.Utilization.Single(u => u.EquipmentId == _line && u.Use == "down").SharePct);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public async Task One_piece_of_equipment_can_be_reported_alone_and_a_run_without_an_ideal_rate_is_noted()
    {
        var report = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, _capper), CancellationToken.None);
        Assert.Equal(["CAP"], report.Equipment.Select(e => e.Code));
        Assert.Equal(0, report.Equipment[0].Depth);

        Oee.AddRun(new(Guid.CreateVersion7(), _capper, At(7), At(8), _material, "M-1", "Widget", _kg, null, null, "J-2"));
        report = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, _capper), CancellationToken.None);
        Assert.Equal((decimal?)null, report.Equipment[0].Figures.PerformancePct);
        Assert.Contains("J-2", Assert.Single(report.Notes), StringComparison.Ordinal);

        await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, Guid.CreateVersion7()), CancellationToken.None));
    }

    [Fact]
    public async Task Time_in_a_job_that_counts_as_planned_downtime_is_left_out_of_planned_time()
    {
        // Planned maintenance on the filler over its jam: the hour is planned, not lost.
        Oee.AddRun(new(Guid.CreateVersion7(), _filler, At(8), At(9), null, null, null, null, null, null, "PM-1", CountsAsPlannedDowntime: true));

        var report = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, _filler), CancellationToken.None);

        var filler = report.Equipment[0].Figures;
        Assert.Equal((1m, 7m, 0m, 100m), (filler.PlannedStopHours, filler.PlannedHours, filler.DownHours, filler.AvailabilityPct));
        Assert.Empty(report.Products);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public async Task Days_still_to_come_are_left_out_and_the_range_is_at_most_31_days()
    {
        var report = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day.AddDays(40), Day.AddDays(-10), null), CancellationToken.None);

        Assert.Equal((Day.AddDays(10), Day.AddDays(40)), (report.From, report.To));
        Assert.Equal(0m, report.Equipment[0].Figures.ScheduledHours);
        Assert.Empty(report.Shifts);
    }
}
