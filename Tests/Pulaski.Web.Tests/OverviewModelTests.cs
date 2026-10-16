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
}
