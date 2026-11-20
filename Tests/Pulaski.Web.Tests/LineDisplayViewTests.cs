using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>The words of a line display board (#120).</summary>
public class LineDisplayViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Durations_read_as_seconds_minutes_or_hours_and_minutes()
    {
        Assert.Null(LineDisplayView.Duration(null, Now));
        Assert.Equal("45 s", LineDisplayView.Duration(Now.AddSeconds(-45), Now));
        Assert.Equal("12 min", LineDisplayView.Duration(Now.AddMinutes(-12.5), Now));
        Assert.Equal("2 h 05 min", LineDisplayView.Duration(Now.AddMinutes(-125), Now));
        Assert.Equal("0 s", LineDisplayView.Duration(Now.AddSeconds(5), Now));
        Assert.Equal(("45 min", "21 h 02 min"), (LineDisplayView.Minutes(44.6m), LineDisplayView.Minutes(1262.3m)));
    }

    [Fact]
    public void Only_the_abnormal_is_coloured_and_oee_below_target_is_a_caution()
    {
        Assert.Equal(("Down", "is-down"), (LineDisplayView.StatusLabel("down"), LineDisplayView.StatusClass("down")));
        Assert.Equal(("No signal", "is-nosignal"), (LineDisplayView.StatusLabel("no_signal"), LineDisplayView.StatusClass("no_signal")));
        Assert.Equal(("Idle", "is-idle"), (LineDisplayView.StatusLabel("idle"), LineDisplayView.StatusClass("idle")));

        LineDisplayLineDto Line(decimal? oee, decimal? target) => new() { LineId = Guid.NewGuid(), Code = "EXT-01", Status = "running", OeePct = oee, OeeTargetPct = target };
        Assert.True(LineDisplayView.BelowTarget(Line(61m, 75m)));
        Assert.False(LineDisplayView.BelowTarget(Line(80m, 75m)));
        Assert.False(LineDisplayView.BelowTarget(Line(61m, null)));
        Assert.False(LineDisplayView.BelowTarget(Line(null, 75m)));
    }

    [Fact]
    public void Progress_layout_and_figures()
    {
        Assert.Equal(25, LineDisplayView.Progress(new LineDisplayJobDto { JobNumber = "J", PlannedQty = 4000m, GoodQty = 1000m }));
        Assert.Equal(100, LineDisplayView.Progress(new LineDisplayJobDto { JobNumber = "J", PlannedQty = 100m, GoodQty = 130m }));
        Assert.Null(LineDisplayView.Progress(new LineDisplayJobDto { JobNumber = "J", GoodQty = 5m }));
        Assert.Equal(("board-grid-1", "board-grid-2", "board-grid-3"), (LineDisplayView.GridClass(1), LineDisplayView.GridClass(4), LineDisplayView.GridClass(5)));
        Assert.Equal(("12,345.7", "73%", "—"), (LineDisplayView.Qty(12345.67m), LineDisplayView.Pct(72.6m), LineDisplayView.Pct(null)));
    }
}
