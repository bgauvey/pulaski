using Pulaski.Contracts.Quality;
using Pulaski.Web.Services.Quality;

namespace Pulaski.Web.Tests;

/// <summary>Control charts and their limits, in words (D-099).</summary>
public class ControlChartViewTests
{
    [Fact]
    public void Each_kind_offers_its_own_charts_and_says_what_the_size_means()
    {
        Assert.Equal(["i_mr", "xbar_r", "xbar_s"], ControlChartView.ChartsFor("variable").Select(c => c.Code));
        Assert.Equal(["p", "np", "c", "u"], ControlChartView.ChartsFor("attribute").Select(c => c.Code));
        Assert.Equal("X-bar and R, subgroups of 5", ControlChartView.Chart("xbar_r", 5));
        Assert.Equal("np chart, 50 inspected", ControlChartView.Chart("np", 50));
        Assert.Equal("Individuals and moving range", ControlChartView.Chart("i_mr", 1));
        Assert.Null(ControlChartView.SizeLabel("p"));
        Assert.Equal("Range", ControlChartView.DispersionName("xbar_r"));
        Assert.Null(ControlChartView.DispersionName("u"));
    }

    [Fact]
    public void Limits_show_two_more_decimals_and_say_how_they_were_set()
    {
        Assert.Equal("3.1954", ControlChartView.Number(3.19540001, 2));
        Assert.Equal("—", ControlChartView.Number(double.NaN, 2));
        Assert.Equal("—", ControlChartView.Number(null, 2));

        var computed = new ControlLimitDto { Method = "computed", BaselineSubgroups = 23, LeftOutSampleIds = [Guid.CreateVersion7()], IsInEffect = true };
        Assert.Equal("From 23 subgroups, 1 left out", ControlChartView.How(computed));
        Assert.Equal("Entered by hand", ControlChartView.How(computed with { Method = "manual" }));
        Assert.Equal("In force", ControlChartView.State(computed, DateTimeOffset.UtcNow));
        Assert.Equal("Starts later", ControlChartView.State(computed with { IsInEffect = false, EffectiveFrom = DateTimeOffset.UtcNow.AddDays(1) }, DateTimeOffset.UtcNow));
    }
}
