using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Partial receipts against an expected delivery, and lead times on shortages (#83).</summary>
public class PartialReceiptTests
{
    private static readonly DateTimeOffset Now = HandlerFixture.Now;

    private static ExpectedReceipt Expected(decimal quantity) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), quantity, Guid.CreateVersion7(), Now.AddDays(2), "PO-1", null, null,
        ExpectedReceipt.Open, null, null, null, Now, "test");

    [Fact]
    public void Each_receipt_counts_toward_it_and_it_stays_open_until_all_has_arrived()
    {
        var lot1 = Guid.CreateVersion7();
        var part = Expected(100m).Receive(lot1, 40m);
        Assert.Equal((ExpectedReceipt.Open, 40m, 60m, lot1), (part.Status, part.ReceivedQty, part.Remaining, part.ReceivedLotId!.Value));

        var whole = part.Receive(Guid.CreateVersion7(), 60m);
        Assert.Equal((ExpectedReceipt.Received, 0m), (whole.Status, whole.Remaining));

        // More than expected closes it too.
        Assert.Equal(ExpectedReceipt.Received, Expected(10m).Receive(lot1, 12m).Status);
        Assert.Throws<DomainRuleException>(() => whole.Receive(lot1, 1m));
    }

    [Fact]
    public void A_partly_received_one_is_closed_as_complete_not_cancelled_and_never_expects_less_than_arrived()
    {
        var part = Expected(100m).Receive(Guid.CreateVersion7(), 40m);

        Assert.Throws<DomainRuleException>(() => part.Cancel());
        Assert.Throws<DomainRuleException>(() => part.Change(40m, part.UomId, part.ExpectedAt, null, null, null));
        Assert.Throws<DomainRuleException>(() => part.Change(80m, Guid.CreateVersion7(), part.ExpectedAt, null, null, null));
        Assert.Equal(80m, part.Change(80m, part.UomId, part.ExpectedAt, null, null, null).Quantity);

        var closed = part.Complete();
        Assert.Equal((ExpectedReceipt.Received, 40m, 0m), (closed.Status, closed.ReceivedQty, closed.Remaining));
        Assert.Throws<DomainRuleException>(() => Expected(5m).Complete());
    }

    [Fact]
    public void A_shortage_needed_within_the_lead_time_cannot_arrive_in_time()
    {
        var soon = new ShortageLine(new ComponentDemand(Guid.NewGuid(), "J1", Guid.NewGuid(), Now.AddDays(3), 10m), 10m, Uncovered: 4m);
        var later = soon with { Demand = soon.Demand with { NeededFrom = Now.AddDays(10) } };

        Assert.True(ShortageCalculator.CantArriveInTime(soon, leadTimeDays: 5, Now));
        Assert.False(ShortageCalculator.CantArriveInTime(later, leadTimeDays: 5, Now));
        Assert.False(ShortageCalculator.CantArriveInTime(soon with { Uncovered = 0m }, leadTimeDays: 5, Now));
        Assert.False(ShortageCalculator.CantArriveInTime(soon, leadTimeDays: null, Now));
        Assert.False(ShortageCalculator.CantArriveInTime(soon with { Demand = soon.Demand with { NeededFrom = null } }, leadTimeDays: 5, Now));
    }
}
