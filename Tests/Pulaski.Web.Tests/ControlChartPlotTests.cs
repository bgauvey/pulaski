using Pulaski.Contracts.Quality;
using Pulaski.Web.Services.Quality;

namespace Pulaski.Web.Tests;

/// <summary>A control chart's plots and words (D-099).</summary>
public class ControlChartPlotTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 11, 14, 30, 0, TimeSpan.Zero);

    private static ControlChartDto Chart(params ControlChartPointDto[] points) => new()
    {
        ChartType = "i_mr",
        Decimals = 1,
        Points = points,
    };

    private static ControlChartPointDto Point(double value, double? ucl, bool beyond = false) => new()
    {
        SampleId = Guid.CreateVersion7(),
        At = At,
        Value = value,
        Dispersion = 1,
        Center = ucl is null ? null : 11,
        Ucl = ucl,
        Lcl = ucl is null ? null : 5.7,
        DispersionUcl = ucl is null ? null : 3.3,
        IsBeyondLimits = beyond,
        LineCode = "EXT-01",
        JobNumber = "WO-1-10",
    };

    [Fact]
    public void The_plots_take_the_statistic_or_its_dispersion_with_their_limits()
    {
        var chart = Chart(Point(10, null), Point(20, 16.3, beyond: true));
        var main = ControlChartView.Plot(chart, false, "UTC");
        Assert.Equal([10d, 20d], main.Select(p => p.Value!.Value));
        Assert.Equal([false, true], main.Select(p => p.IsBeyond));
        Assert.Null(main[0].Ucl);
        Assert.Equal(3.3, ControlChartView.Plot(chart, true, "UTC")[1].Ucl);
        Assert.Equal("2026-11-11 14:30: 20.000 (limits 5.700 to 16.300, beyond them). EXT-01 · WO-1-10", main[1].Tip);
        Assert.EndsWith("(no limits in force). EXT-01 · WO-1-10", main[0].Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_counts_what_is_beyond_and_what_has_no_limits()
    {
        Assert.Equal("2 results; 1 beyond the limits. 1 had no limits in force.", ControlChartView.Summary(Chart(Point(10, null), Point(20, 16.3, beyond: true))));
        Assert.Equal("1 results; no control limits in force for them.", ControlChartView.Summary(Chart(Point(10, null))));
        Assert.EndsWith("Only the latest 500 are shown.", ControlChartView.Summary(Chart(Point(10, 16.3)) with { IsTruncated = true, MaxPoints = 500 }), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_rules_other_than_one_are_marked_named_and_counted()
    {
        var chart = Chart(Point(10, 16.3) with { Rules = [2, 5] }, Point(20, 16.3, beyond: true) with { Rules = [1] });
        var main = ControlChartView.Plot(chart, false, "UTC");
        Assert.Equal([2, 5], main[0].Rules);
        Assert.Empty(main[1].Rules);
        Assert.EndsWith(". Rule 2: 9 in a row on one side of the centre line. Rule 5: 2 of 3 beyond 2 sigma on one side", main[0].Tip, StringComparison.Ordinal);
        Assert.Empty(ControlChartView.Plot(chart, true, "UTC")[0].Rules);
        Assert.Equal("2 results; 1 beyond the limits, 1 breaking a run rule.", ControlChartView.Summary(chart));
        Assert.Equal("Rules 2, 5", ControlChartView.RuleList([2, 5]));
        Assert.Equal("Rule 8", ControlChartView.RuleList([8]));
    }
}
