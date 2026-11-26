using Pulaski.Web.Services.Quality;

namespace Pulaski.Web.Tests;

/// <summary>Carrying out a closed NCR's disposition offers the next step for each lot still in stock (#133b).</summary>
public class NcrViewTests
{
    [Theory]
    [InlineData("use_as_is", "on_hold", 5, "release")]
    [InlineData("use_as_is", "available", 5, null)]
    [InlineData("scrap", "available", 5, "hold")]
    [InlineData("scrap", "quarantine", 5, "reject")]
    [InlineData("rework", "available", 5, "hold")]
    [InlineData("return_to_supplier", "on_hold", 5, null)]
    [InlineData("scrap", "on_hold", 0, null)]
    [InlineData("scrap", "shipped", 5, null)]
    [InlineData("not_applicable", "on_hold", 5, null)]
    public void The_next_step_follows_the_disposition_and_the_lots_status(string disposition, string status, int quantity, string? expected) =>
        Assert.Equal(expected, NcrView.NextStep(disposition, status, quantity));
}
