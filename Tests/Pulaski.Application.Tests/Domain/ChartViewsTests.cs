using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Tests.Domain;

/// <summary>A subgroup chart shows its means, ranges or standard deviations, both from the same sigma (#198).</summary>
public class ChartViewsTests
{
    [Fact]
    public void Each_kind_of_chart_has_its_views_means_first()
    {
        Assert.Equal(["location", "range", "std_dev"], ChartViews.For("xbar_r"));
        Assert.Equal(["location", "range", "std_dev"], ChartViews.For("xbar_s"));
        Assert.Equal(["location", "moving_range"], ChartViews.For("i_mr"));
        Assert.Equal(["location"], ChartViews.For("p"));
    }

    [Fact]
    public void An_xbar_r_chart_keeps_its_range_limits_and_gets_standard_deviation_limits_from_the_same_sigma()
    {
        // n = 5: d2 2.326, c4 0.9400, B3 0, B4 2.089. R-bar 2.326 means sigma 1, so S-bar 0.94.
        var (range, stdDev) = DispersionLimits.For("xbar_r", new LimitSet(10, 11.3, 8.7, 2.326, 4.918, 0), 5);
        Assert.Equal(new LimitLine(2.326, 4.918, 0), range);
        Assert.Equal(ChartConstants.StdDevBias(5), stdDev!.Center, 1e-9);
        Assert.Equal(ChartConstants.B4(5) * ChartConstants.StdDevBias(5), stdDev.Ucl, 1e-9);
        Assert.Equal(0, stdDev.Lcl, 1e-9);
    }

    [Fact]
    public void An_xbar_s_chart_keeps_its_standard_deviation_limits_and_gets_range_limits_from_the_same_sigma()
    {
        var sBar = ChartConstants.StdDevBias(5);
        var (range, stdDev) = DispersionLimits.For("xbar_s", new LimitSet(10, 11.3, 8.7, sBar, 2.089 * sBar, 0), 5);
        Assert.Equal(sBar, stdDev!.Center, 1e-9);
        Assert.Equal(ChartConstants.RangeBias(5), range!.Center, 1e-9);
        Assert.Equal(ChartConstants.D4(5) * ChartConstants.RangeBias(5), range.Ucl, 1e-9);
    }

    [Fact]
    public void Other_charts_and_limits_without_dispersion_have_none() =>
        Assert.Equal((null, null), DispersionLimits.For("i_mr", new LimitSet(10, 12, 8, 1, 3.267, 0), 1));
}
