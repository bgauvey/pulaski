using Pulaski.Contracts.MachineData;
using Pulaski.Web.Services.MachineData;

namespace Pulaski.Web.Tests;

public class ProcessValueViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_mean_weighs_each_reading_by_how_long_it_held_and_lost_readings_are_counted_apart()
    {
        // 200 for 30 min, 210 for 10 min, then the signal was lost for 20 min.
        var summary = ProcessValueView.Summarize(
        [
            new(T0, 200, true),
            new(T0.AddMinutes(30), 210, true),
            new(T0.AddMinutes(40), null, false),
        ], T0.AddMinutes(60));

        Assert.Equal((3, 1, 200d, 210d, 202.5d), (summary.Readings, summary.Lost, summary.Min, summary.Max, summary.Mean));
        Assert.False(summary.Latest!.Good);
        Assert.Equal("202.5", ProcessValueView.Value(summary.Mean));
        Assert.Equal("—", ProcessValueView.Value(null));
    }

    [Fact]
    public void No_readings_have_no_figures()
    {
        var summary = ProcessValueView.Summarize([], T0);
        Assert.Equal((0, (ProcessValuePointDto?)null, (double?)null), (summary.Readings, summary.Latest, summary.Mean));
    }
}
