using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Application.MachineData;
using Pulaski.Domain.MachineData;
using Pulaski.Domain.Shifts;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.MachineData;

/// <summary>
/// The utilization and OEE report (D-052). The fixture's clock is Monday 2026-10-12 14:00 UTC, the end of the line's day shift:
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

    private GetOeeReportHandler Handler() => new(Oee, _f.Stores.OeeRollups, _f.Stores.Units, _f.Time);

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
        Assert.Equal((7.5m, 6.5m, 6.5m, 5.3m, 5m), (line.PlannedHoursForOee, line.RunHoursForAvailability, line.RatedRunHoursForPerformance, line.IdealHours, line.GoodIdealHours));

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
    public async Task One_shift_keeps_only_its_windows_their_stops_and_the_output_reported_in_them()
    {
        var all = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, null), CancellationToken.None);
        var shiftA = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, null, "A"), CancellationToken.None);
        var other = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, null, "N"), CancellationToken.None);

        // Shift A is every window here, so it is the whole report; a shift not worked has nothing.
        Assert.Equal(all.Equipment.Select(e => e.Figures), shiftA.Equipment.Select(e => e.Figures));
        Assert.Equal(all.Utilization.Count, shiftA.Utilization.Count);
        Assert.Equal((0m, 0, (decimal?)null), (other.Equipment[0].Figures.ScheduledHours, other.Equipment[0].Figures.Stops, other.Equipment[0].Figures.QualityPct));
        Assert.Empty(other.Shifts);
        Assert.Empty(other.Utilization);
        Assert.Empty(other.Products);
    }

    [Fact]
    public async Task Days_still_to_come_are_left_out_and_a_range_is_at_most_366_days()
    {
        var report = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day.AddDays(40), Day.AddDays(10), null), CancellationToken.None);

        Assert.Equal((Day.AddDays(10), Day.AddDays(40), false), (report.From, report.To, report.FromRollups));
        Assert.Equal(0m, report.Equipment[0].Figures.ScheduledHours);
        Assert.Empty(report.Shifts);

        var year = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day.AddDays(-400), Day.AddDays(40), null), CancellationToken.None);
        Assert.Equal((Day.AddDays(40 - 365), Day.AddDays(40), true), (year.From, year.To, year.FromRollups));
    }

    [Fact]
    public async Task A_day_rolled_up_gives_the_figures_the_history_gives()
    {
        // Three days on, the day is final.
        _f.Time.SetUtcNow(HandlerFixture.Now.AddDays(3));
        Oee.AddCount(_line, new(_run, At(15), _kg, 10m, 0m));    // after the shift: still the day's
        var onRead = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, null), CancellationToken.None);

        await Rollups().RollDayAsync(new OeeDayToRoll(_site, Day, 0, null), CancellationToken.None);
        var rolled = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, null, FromRollups: true), CancellationToken.None);

        Assert.True(rolled.FromRollups && rolled.ByDay);
        Assert.Equal(onRead.Equipment.Select(e => e.Figures), rolled.Equipment.Select(e => e.Figures));
        Assert.Equal(onRead.Products.Select(p => (p.MaterialCode, p.GoodQty, p.ScrapQty, p.Runs, p.Figures)),
            rolled.Products.Select(p => (p.MaterialCode, p.GoodQty, p.ScrapQty, p.Runs, p.Figures)));
        Assert.Equal(onRead.Utilization, rolled.Utilization, new UtilizationRowComparer());
        Assert.Equal(onRead.Shifts.Select(s => (s.ProductionDate, s.EquipmentId, s.Figures)), rolled.Shifts.Select(s => (s.ProductionDate, s.EquipmentId, s.Figures)));
        Assert.Equal(510m, rolled.Products.Single().GoodQty);
    }

    [Fact]
    public async Task Days_marked_or_rolled_before_a_settings_change_are_worked_out_again_and_beyond_31_left_out()
    {
        _f.Time.SetUtcNow(HandlerFixture.Now.AddDays(3));
        await Rollups().RollDayAsync(new OeeDayToRoll(_site, Day, 0, null), CancellationToken.None);
        var rolls = _f.Stores.OeeRollups;

        // Rolled, then a late change marks it: it is worked out from the history again until it is rolled again.
        rolls.Mark(_site, Day, HandlerFixture.Now.AddDays(3));
        Assert.Empty(await rolls.ListCurrentDaysAsync(_site, Day, Day, CancellationToken.None));
        await Rollups().RollDayAsync(new OeeDayToRoll(_site, Day, 0, HandlerFixture.Now.AddDays(3)), CancellationToken.None);
        Assert.Single(await rolls.ListCurrentDaysAsync(_site, Day, Day, CancellationToken.None));

        // A settings change makes every rolled day of the site out of date.
        rolls.SiteChanged(_site);
        Assert.Empty(await rolls.ListCurrentDaysAsync(_site, Day, Day, CancellationToken.None));

        // 40 days and none current: the newest 31 are worked out now, the 9 before are left out and named.
        var report = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day.AddDays(-37), Day.AddDays(2), null), CancellationToken.None);
        Assert.Equal([.. Enumerable.Range(0, 9).Select(d => Day.AddDays(-37 + d))], report.LeftOutDays);
        Assert.Contains(report.Notes, n => n.StartsWith("9 days aren't included", StringComparison.Ordinal));
        Assert.Equal(6.5m, report.Shifts.Single(s => s.ProductionDate == Day && s.EquipmentId == _line).Figures.RunHours);
    }

    [Fact]
    public async Task Figures_carry_the_target_of_their_day()
    {
        Oee.Targets.Add(new(Guid.CreateVersion7(), _site, null, Day.AddDays(-30), null, new(null, null, null, 50m)));
        Oee.Targets.Add(new(Guid.CreateVersion7(), _site, _capper, Day, null, new(95m, null, null, null)));

        var report = await Handler().HandleAsync(new GetOeeReportQuery(_site, Day, Day, null), CancellationToken.None);

        Assert.Equal(50m, report.Equipment.Single(e => e.EquipmentId == _line).Target!.OeePct);
        Assert.Equal((95m, (decimal?)null), (report.Equipment.Single(e => e.EquipmentId == _capper).Target!.AvailabilityPct,
            report.Equipment.Single(e => e.EquipmentId == _capper).Target!.OeePct));
        Assert.All(report.Shifts.Where(s => s.EquipmentId == _line), s => Assert.Equal(50m, s.Target!.OeePct));
    }

    private OeeRollups Rollups() => new(Oee, _f.Stores.OeeRollups, _f.Stores.OeeRollups, _f.Stores.Units, _f.Stores.UnitOfWork, _f.Time);

    private sealed class UtilizationRowComparer : IEqualityComparer<Contracts.MachineData.UtilizationRowDto>
    {
        public bool Equals(Contracts.MachineData.UtilizationRowDto? x, Contracts.MachineData.UtilizationRowDto? y) =>
            (x?.EquipmentId, x?.Use, x?.ReasonCodeId, x?.NoSignal, x?.Hours, x?.SharePct, x?.Stops) == (y?.EquipmentId, y?.Use, y?.ReasonCodeId, y?.NoSignal, y?.Hours, y?.SharePct, y?.Stops);

        public int GetHashCode(Contracts.MachineData.UtilizationRowDto obj) => HashCode.Combine(obj.EquipmentId, obj.Use, obj.ReasonCodeId);
    }
}
