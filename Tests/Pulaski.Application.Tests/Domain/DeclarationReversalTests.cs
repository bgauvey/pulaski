using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Tests.Domain;

/// <summary>The compensating movements reversals make (D-063).</summary>
public class DeclarationReversalTests
{
    private static Lot L(decimal quantity, string status = LotStatus.Available, bool bulk = false) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), bulk ? null : "L-1", status, quantity, Guid.CreateVersion7(),
            Guid.CreateVersion7(), bulk, LotOrigin.Produced);

    [Fact]
    public void Stock_given_back_reopens_a_used_up_lot_and_a_merge_source_gets_its_status_back()
    {
        var (lot, movement) = L(0m, LotStatus.Consumed).Restore(30m);
        Assert.Equal((30m, LotStatus.Available, LotEventKind.Reversed, 30m), (lot.Quantity, lot.Status, movement.Kind, movement.QtyDelta!.Value));

        var (held, heldMovement) = L(0m, LotStatus.Consumed).Restore(10m, LotStatus.OnHold);
        Assert.Equal((LotStatus.OnHold, LotStatus.Consumed, LotStatus.OnHold), (held.Status, heldMovement.FromStatus, heldMovement.ToStatus));
        Assert.Throws<DomainRuleException>(() => L(0m, LotStatus.Shipped).Restore(5m));
    }

    [Fact]
    public void Stock_taken_back_must_still_be_there_and_a_lot_that_only_held_it_is_cancelled()
    {
        Assert.Equal(LotStatus.Cancelled, L(50m).TakeBack(50m, cancelWhenEmpty: true, "m").Lot.Status);
        Assert.Equal(LotStatus.Consumed, L(50m).TakeBack(50m, cancelWhenEmpty: false, "m").Lot.Status);
        Assert.Equal(LotStatus.Available, L(50m, bulk: true).TakeBack(50m, cancelWhenEmpty: true, "m").Lot.Status);
        Assert.Equal(20m, L(50m).TakeBack(30m, true, "m").Lot.Quantity);
        Assert.Contains("no longer holds", Assert.Throws<DomainRuleException>(() => L(20m).TakeBack(30m, true, "m")).Message, StringComparison.Ordinal);
        Assert.Throws<DomainRuleException>(() => L(30m, LotStatus.Shipped).TakeBack(30m, true, "m"));
    }

    [Fact]
    public void A_reversed_report_comes_off_its_run_and_its_partial_batch_stops_counting_unless_rejected()
    {
        var run = JobRun.Open(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, HandlerFixture.Now, null).Add(100m, 10m);
        Assert.Equal((60m, 5m), (run.Remove(40m, 5m).GoodQty, run.Remove(40m, 5m).ScrapQty));

        var partial = new PartialBatch(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 20m, Guid.CreateVersion7(),
            50m, "exact", PartialBatchStatus.Pending, HandlerFixture.Now, "op");
        Assert.Equal(PartialBatchStatus.Reversed, partial.Reverse().Status);
        Assert.Throws<DomainRuleException>(() => (partial with { Status = PartialBatchStatus.Rejected }).Reverse());
        Assert.Throws<DomainRuleException>(() => partial.Reverse().Reverse());
    }
}
