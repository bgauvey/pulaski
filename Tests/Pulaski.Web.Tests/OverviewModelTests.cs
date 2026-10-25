using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Overview;
using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

public class OverviewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 14, 25, 0, TimeSpan.Zero);

    private static TimelineSegmentDto Segment(double startHours, double endHours, string kind = "job", string type = "production", decimal planned = 1000m, decimal remaining = 1000m) => new()
    {
        Kind = kind,
        JobId = Guid.CreateVersion7(),
        JobNumber = $"J-{startHours}",
        JobType = type,
        Status = "planned",
        Start = Now.AddHours(startHours),
        End = Now.AddHours(endHours),
        PlannedQty = planned,
        RemainingQty = remaining,
    };

    [Fact]
    public void The_window_runs_six_hours_back_and_eighteen_ahead_on_whole_hours()
    {
        var window = OverviewWindow.Around(Now);

        Assert.Equal((Now.AddMinutes(-25).AddHours(-6), Now.AddMinutes(-25).AddHours(18)), (window.Start, window.End));
        Assert.Equal(25d + 25d / 60d / 24d * 100d, window.Percent(Now), 6);
        Assert.Equal(0d, window.Percent(Now.AddDays(-2)));
        Assert.Equal(8, window.Ticks("UTC").Count);
        Assert.Equal("08:00", window.Ticks("UTC")[0].Label);
    }

    [Fact]
    public void Now_or_next_prefers_the_running_job_then_the_next_production_job()
    {
        var past = Segment(-3, -1);
        var running = Segment(-1, 2);
        var changeover = Segment(2, 2.5, kind: "changeover_placeholder", type: "changeover");
        var next = Segment(2.5, 5);

        Assert.Same(running, OverviewModel.NowOrNext([past, running, changeover, next], Now));
        Assert.Same(next, OverviewModel.NowOrNext([past, changeover, next], Now));
        Assert.Null(OverviewModel.NowOrNext([past], Now));
    }

    [Fact]
    public void Bars_are_cut_to_the_window_and_keep_a_visible_width()
    {
        var window = OverviewWindow.Around(Now);
        var bars = OverviewModel.Bars([Segment(-30, -20), Segment(-8, 1), Segment(3, 3.001), Segment(10, 40)], window);

        Assert.Equal(3, bars.Count);
        Assert.Equal(0d, bars[0].Left);
        Assert.Equal(0.4d, bars[1].Width);
        Assert.Equal(100d, bars[2].Left + bars[2].Width, 6);
    }

    [Fact]
    public void Made_is_the_share_of_the_planned_quantity_already_made()
    {
        Assert.Equal(0.25d, OverviewModel.Made(Segment(0, 1, planned: 1000m, remaining: 750m)));
        Assert.Equal(0d, OverviewModel.Made(Segment(0, 1, planned: 0m, remaining: 0m)));
    }

    [Fact]
    public void Current_shift_oee_combines_line_components_instead_of_averaging_percentages()
    {
        var firstLine = Shift(Guid.CreateVersion7(), "L1", Now.AddHours(-4), Now.AddHours(4), true, new OeeFiguresDto
        {
            PlannedHoursForOee = 8m,
            RunHoursForAvailability = 4m,
            RatedRunHoursForPerformance = 4m,
            IdealHours = 2m,
            GoodIdealHours = 1m,
        });
        var secondLine = Shift(Guid.CreateVersion7(), "L2", Now.AddHours(-2), Now.AddHours(2), true, new OeeFiguresDto
        {
            PlannedHoursForOee = 2m,
            RunHoursForAvailability = 2m,
            RatedRunHoursForPerformance = 2m,
            IdealHours = 2m,
            GoodIdealHours = 2m,
        });
        var machine = Shift(Guid.CreateVersion7(), "M1", Now.AddHours(-1), Now.AddHours(1), false, new OeeFiguresDto
        {
            PlannedHoursForOee = 1m,
            RunHoursForAvailability = 0m,
            RatedRunHoursForPerformance = 1m,
            IdealHours = 1m,
            GoodIdealHours = 0m,
        });

        var oee = OverviewModel.CurrentShiftOee([firstLine, secondLine, machine], Now);

        Assert.Equal(new OverviewOee(60m, 66.7m, 75m, 30m), oee);
        Assert.Same(secondLine, OverviewModel.CurrentShift([firstLine, secondLine], secondLine.EquipmentId, Now));
    }

    [Fact]
    public void Current_shift_lookup_excludes_a_shift_at_its_end_and_summary_without_a_current_line_is_empty()
    {
        var ended = Shift(Guid.CreateVersion7(), "L1", Now.AddHours(-2), Now, true, new OeeFiguresDto());

        Assert.Null(OverviewModel.CurrentShift([ended], ended.EquipmentId, Now));
        Assert.Null(OverviewModel.CurrentShiftOee([ended], Now));
    }

    private static OeeShiftRowDto Shift(
        Guid equipmentId, string code, DateTimeOffset start, DateTimeOffset end, bool canRunJob, OeeFiguresDto figures) =>
        new()
        {
            EquipmentId = equipmentId,
            EquipmentCode = code,
            Start = start,
            End = end,
            CanRunJob = canRunJob,
            Figures = figures,
        };

    private static OverviewLineDto Line(params (string Uom, decimal Good, decimal Scrap)[] output) => new()
    {
        EquipmentId = Guid.CreateVersion7(),
        Code = "L",
        TracksState = true,
        From = Now.AddHours(-2),
        Output = [.. output.Select(o => new OverviewOutputDto { UomCode = o.Uom, GoodQty = o.Good, ScrapQty = o.Scrap })],
    };

    [Fact]
    public void A_shift_window_from_the_api_ticks_every_two_hours()
    {
        var window = OverviewWindow.Of(new OverviewDto { SiteId = Guid.Empty, WindowStart = Now.AddHours(-2), WindowEnd = Now.AddHours(6), WindowIsShift = true });

        Assert.Equal((2, 4), (window.TickStep, window.Ticks("UTC", window.TickStep).Count));
        Assert.Equal(3, OverviewWindow.Around(Now).TickStep);
    }

    [Fact]
    public void The_plan_for_a_shift_shares_each_job_over_its_projected_time()
    {
        var half = Segment(-4, 0) with { UomCode = "m", PlannedQty = 800m };      // half of it inside the shift
        var whole = Segment(0, 2) with { UomCode = "m", PlannedQty = 300m };
        var kilos = Segment(1, 10) with { UomCode = "kg", PlannedQty = 900m };    // 5 of its 9 hours
        var changeover = Segment(0, 1, kind: "changeover_placeholder", type: "changeover") with { UomCode = "m" };

        var planned = OverviewModel.Planned([half, whole, kilos, changeover], Now.AddHours(-2), Now.AddHours(6));

        Assert.Equal([("m", 700m), ("kg", 500m)], planned.Select(p => (p.UomCode, Math.Round(p.Qty, 6))));
    }

    [Fact]
    public void Good_and_scrap_add_up_by_unit_against_plan_and_output()
    {
        var lines = new[] { Line(("m", 400m, 10m)), Line(("m", 200m, 30m), ("kg", 50m, 0m)) };

        Assert.Equal([("kg", 50m, 0m), ("m", 600m, 1000m), ("pcs", 0m, 20m)],
            OverviewModel.Good(lines, [new("m", 700m), new("m", 300m), new("pcs", 20m)]).Select(q => (q.UomCode, q.Qty, q.Of)));
        Assert.Equal([("kg", 0m, 50m), ("m", 40m, 640m)], OverviewModel.Scrap(lines).Select(q => (q.UomCode, q.Qty, q.Of)));
    }

    [Fact]
    public void The_top_reason_adds_up_across_lines()
    {
        var a = Line() with { DownReasons = [new() { ReasonCode = "JAM", Name = "Jam", Minutes = 12m }, new() { Name = "No signal", NoSignal = true, Minutes = 20m }] };
        var b = Line() with { DownReasons = [new() { ReasonCode = "JAM", Name = "Jam", Minutes = 9m }] };

        Assert.Equal(("JAM", 21m), (OverviewModel.TopReason([a, b])?.ReasonCode, OverviewModel.TopReason([a, b])?.Minutes));
        Assert.Null(OverviewModel.TopReason([Line()]));
    }

    [Fact]
    public void State_bars_are_cut_to_the_window()
    {
        var window = new OverviewWindow(Now.AddHours(-2), Now.AddHours(6));
        var bars = OverviewModel.StateBars(
        [
            new() { Start = Now.AddHours(-5), End = Now.AddHours(-3), Use = "run" },
            new() { Start = Now.AddHours(-3), End = Now.AddHours(-1), Use = "down" },
            new() { Start = Now.AddHours(-1), End = Now, Use = "run" },
        ], window);

        Assert.Equal([("down", 0d, 12.5d), ("run", 12.5d, 12.5d)], bars.Select(b => (b.Span.Use, Math.Round(b.Left, 6), Math.Round(b.Width, 6))));
    }
}
