using Pulaski.Contracts.MachineData;
using Pulaski.Web.Services.MachineData;

namespace Pulaski.Web.Tests;

/// <summary>The OEE trend's points (#67) and targets on screen (D-073).</summary>
public class OeeTrendTests
{
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly DateOnly Day = new(2026, 10, 26);

    private static OeeShiftRowDto Shift(DateOnly day, string code, int hour, decimal planned, decimal run, decimal? target) => new()
    {
        ProductionDate = day,
        ShiftCode = code,
        Start = new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero),
        End = new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero).AddHours(8),
        EquipmentId = Line,
        EquipmentCode = "L1",
        Figures = OeeTrend.Combine([new OeeFiguresDto { PlannedHoursForOee = planned, RunHoursForAvailability = run }]),
        Target = target is null ? null : new OeeTargetFiguresDto(target, null, null, null),
    };

    private static OeeReportDto Report() => new()
    {
        Shifts =
        [
            Shift(Day, "A", 6, 8m, 6m, 80m),
            Shift(Day, "B", 14, 2m, 2m, 80m),
            Shift(Day.AddDays(1), "A", 6, 8m, 8m, 90m),
        ],
    };

    [Fact]
    public void Shifts_are_points_and_a_day_adds_its_shifts_hours_up()
    {
        var shifts = OeeTrend.Points(Report(), Line, OeeTrend.Availability, byDay: false);
        Assert.Equal([("Mon 26 Oct A", 75m, true), ("Mon 26 Oct B", 100m, false), ("Tue 27 Oct A", 100m, false)],
            shifts.Select(p => (p.Label, p.Value!.Value, p.Below!.Value)));

        // 8 of 10 planned hours: 80%, not the 87.5% an average of the shifts would give.
        var days = OeeTrend.Points(Report(), Line, OeeTrend.Availability, byDay: true);
        Assert.Equal([(Day, 80m, 80m), (Day.AddDays(1), 100m, 90m)], days.Select(p => (p.Day, p.Value!.Value, p.Target!.Value)));
        Assert.All(days, p => Assert.False(p.Below));

        var (overall, below, measured) = OeeTrend.Summary(Report(), Line, OeeTrend.Availability, shifts);
        Assert.Equal((88.9m, 1, 3), (overall!.Value, below, measured));
    }

    [Fact]
    public void Without_a_target_or_a_figure_a_point_is_neither_above_nor_below()
    {
        var points = OeeTrend.Points(Report(), Line, OeeTrend.Oee, byDay: false);
        Assert.All(points, p => Assert.Null(p.Below));
        Assert.Null(OeeTargetsView.Below(70m, null));
    }

    [Fact]
    public void Targets_in_force_are_the_sites_first_and_the_form_checks_its_figures()
    {
        OeeTargetDto T(Guid? equipment, string? code, DateOnly from, DateOnly? to) => new()
        {
            Id = Guid.CreateVersion7(),
            SiteId = Guid.Empty,
            EquipmentId = equipment,
            EquipmentCode = code,
            EffectiveFrom = from,
            EffectiveTo = to,
            Targets = new(null, null, null, 65m),
        };
        var targets = new[] { T(Line, "L1", Day, null), T(null, null, Day.AddDays(-30), Day), T(null, null, Day, null) };

        Assert.Equal([null, Line], OeeTargetsView.InForce(targets, Day).Select(t => t.EquipmentId));
        Assert.Equal(Day.AddDays(-30), Assert.Single(OeeTargetsView.InForce(targets, Day.AddDays(-1))).EffectiveFrom);
        Assert.Equal(Day, OeeTargetsView.Latest(targets, null)!.EffectiveFrom);
        Assert.Equal("Sat 26 Sep 2026 – Sun 25 Oct 2026", OeeTargetsView.Period(targets[1]));
        Assert.Equal("from Mon 26 Oct 2026", OeeTargetsView.Period(targets[0]));

        Assert.Equal("Give at least one target.", OeeTargetsView.Problem(null, null, null, null));
        Assert.NotNull(OeeTargetsView.Problem(101m, null, null, null));
        Assert.NotNull(OeeTargetsView.Problem(65.25m, null, null, null));
        Assert.Null(OeeTargetsView.Problem(null, null, null, 65.5m));
    }
}
