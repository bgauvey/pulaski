using Pulaski.Web.Services;
using Pulaski.Web.Services.Inventory;

namespace Pulaski.Web.Tests;

/// <summary>Why an action can't be taken now (#47): every switched-off button that depends on state says why.</summary>
public class UnavailableActionTests
{
    [Fact]
    public void A_lots_actions_say_why_they_are_off_from_its_state()
    {
        Assert.Null(LotActionAvailability.Why(LotAction.Consume, "available", 10m, expired: false));
        Assert.Equal("The lot is on hold; release it first.", LotActionAvailability.Why(LotAction.Ship, "on_hold", 10m, false));
        Assert.Equal("The lot is in quarantine; release it first.", LotActionAvailability.Why(LotAction.Consume, "quarantine", 10m, false));
        Assert.Contains("expired", LotActionAvailability.Why(LotAction.Consume, "available", 10m, true), StringComparison.Ordinal);
        Assert.Contains("expired", LotActionAvailability.Why(LotAction.Merge, "available", 10m, true), StringComparison.Ordinal);
        Assert.Equal("None of the lot is left.", LotActionAvailability.Why(LotAction.Split, "available", 0m, false));

        // A held lot can still move or change its hold; one that is gone can do nothing.
        Assert.Null(LotActionAvailability.Why(LotAction.Move, "on_hold", 10m, false));
        Assert.Null(LotActionAvailability.Why(LotAction.Hold, "quarantine", 10m, false));
        Assert.Equal("The lot has been shipped.", LotActionAvailability.Why(LotAction.Move, "shipped", 0m, false));
        Assert.Equal("The lot is used up.", LotActionAvailability.Why(LotAction.WriteOff, "consumed", 0m, false));
        Assert.Equal("The lot isn't made yet.", LotActionAvailability.Why(LotAction.Consume, "planned", 0m, false));

        // Actions that don't depend on state never give one.
        Assert.Null(LotActionAvailability.Why(LotAction.WriteUp, "shipped", 0m, true));
    }

    [Fact]
    public void An_import_says_what_comes_first()
    {
        Assert.StartsWith("Pick a file first", ImportReadiness.Why(false, false, 0, 0, 0), StringComparison.Ordinal);
        Assert.StartsWith("Fix what the check found", ImportReadiness.Why(true, false, 1, 0, 0), StringComparison.Ordinal);
        Assert.StartsWith("Fix what the check found", ImportReadiness.Why(true, false, 0, 2, 3), StringComparison.Ordinal);
        Assert.Equal("The file changes nothing.", ImportReadiness.Why(true, false, 0, 0, 0));
        Assert.StartsWith("Imported.", ImportReadiness.Why(true, true, 0, 0, 3), StringComparison.Ordinal);
        Assert.Null(ImportReadiness.Why(true, false, 0, 0, 3));
    }
}
