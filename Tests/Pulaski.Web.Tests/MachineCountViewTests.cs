using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>What a run's machine counter counted, in words (#27).</summary>
public class MachineCountViewTests
{
    private static readonly MachineCountsDto Counted = new()
    {
        RunId = Guid.CreateVersion7(),
        CounterCode = "EXT-01-SAW",
        Through = new DateTimeOffset(2026, 11, 9, 16, 40, 0, TimeSpan.Zero),
        GoodQty = 348m,
        ScrapQty = 12m,
        UnitsPerCount = 2.4m,
        UomCode = "m",
        RunMachineGoodQty = 372m,
        RunReportedGoodQty = 348m,
        ReportedUomCode = "m",
    };

    [Fact]
    public void Counts_since_the_last_report_are_offered_with_the_run_reconciled()
    {
        Assert.True(MachineCountView.CanUse(Counted));
        var since = MachineCountView.Since(Counted, "UTC");
        Assert.StartsWith("EXT-01-SAW counted 348 good and 12 scrap m since the run started", since, StringComparison.Ordinal);
        Assert.EndsWith("(2.4 m a count).", since, StringComparison.Ordinal);
        Assert.Contains("the machine counted 372 good", MachineCountView.Run(Counted), StringComparison.Ordinal);
        Assert.Contains("reported 348 good", MachineCountView.Run(Counted), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_take_says_why()
    {
        Assert.False(MachineCountView.CanUse(Counted with { Through = null, GoodQty = 0, ScrapQty = 0 }));
        Assert.StartsWith("EXT-01-SAW hasn't counted anything", MachineCountView.Since(Counted with { Through = null, GoodQty = 0, ScrapQty = 0 }, "UTC"), StringComparison.Ordinal);
        var unavailable = Counted with { Unavailable = "Nothing on the line has a processed counter." };
        Assert.False(MachineCountView.CanUse(unavailable));
        Assert.Equal("Nothing on the line has a processed counter.", MachineCountView.Since(unavailable, "UTC"));
        Assert.Null(MachineCountView.Run(unavailable));
    }
}
