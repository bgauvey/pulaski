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

    [Fact]
    public void Sources_and_intervals_read_as_people_say_them()
    {
        Assert.Equal("From process values", ControlChartView.Source("auto"));
        Assert.Equal("Laboratory", ControlChartView.Source("lab"));
        Assert.Equal("Every 15 min", ControlChartView.Interval(15));
        Assert.Equal("Every 2 h", ControlChartView.Interval(120));
        Assert.Equal("Every 1 h 30 min", ControlChartView.Interval(90));
    }

    [Fact]
    public void Capability_reads_with_its_band_specification_and_rate()
    {
        Assert.Equal("1.27", ControlChartView.Index(1.2749));
        Assert.Equal("—", ControlChartView.Index(null));
        Assert.Equal("Not capable", ControlChartView.BandName("not_capable"));
        Assert.Equal("chip chip-ok", ControlChartView.BandChip("capable"));
        Assert.Equal("LSL 3.2 · target 3.3 · USL 3.4 mm, specification version 2",
            ControlChartView.Specification(new CapabilityDto { Lsl = 3.2m, Target = 3.3m, Usl = 3.4m, SpecVersion = 2 }, "mm"));
        Assert.Equal("3.00% defective (30,000 ppm): 6 defective in 200 inspected. The specification allows at most 1 a sample.",
            ControlChartView.Rate(new CapabilityDto { DefectRate = 0.03, Ppm = 30_000, Defects = 6, Inspected = 200, MaxDefects = 1 }, "p"));
    }

    [Fact]
    public void A_refreshed_chart_keeps_up_with_the_present_only_when_its_window_reached_it()
    {
        var loadedAt = new DateTimeOffset(2026, 11, 14, 14, 0, 0, TimeSpan.Zero);
        var now = loadedAt.AddSeconds(30);

        Assert.Equal(now.AddMinutes(1), ControlChartView.RefreshedTo(loadedAt.AddMinutes(1), loadedAt, now));
        Assert.Equal(now.AddMinutes(1), ControlChartView.RefreshedTo(loadedAt, loadedAt, now));
        Assert.Equal(loadedAt.AddHours(-2), ControlChartView.RefreshedTo(loadedAt.AddHours(-2), loadedAt, now));
    }
}
