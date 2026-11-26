using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Inventory;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Tests;

/// <summary>The actions a scan at a line offers read as what they do (#135).</summary>
public class ScanActionViewTests
{
    [Fact]
    public void Each_action_says_what_it_does_and_only_allowed_ones_are_offered()
    {
        var consume = new ScanActionDto { Kind = "consume", Permission = "production.report", Quantity = 1000m, UomCode = "kg", LotLabel = "R-1", JobNumber = "J-1" };
        var start = new ScanActionDto { Kind = "start", Permission = "job.execute", JobNumber = "J-2", ToCode = "EXT-01" };
        Assert.Equal("Consume 1,000 kg of R-1 into J-1", ScanActionView.Title(Text.English, consume));
        Assert.Equal("Start J-2 on EXT-01", ScanActionView.Title(Text.English, start));
        Assert.Equal("250 kg of R-1 consumed into J-1.", ScanActionView.Done(Text.English, consume, 250m));
        Assert.Equal([start], ScanActionView.Allowed([consume, start], p => p == "job.execute"));
    }
}
