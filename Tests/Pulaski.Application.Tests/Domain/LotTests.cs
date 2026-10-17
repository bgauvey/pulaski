using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Lot quantities and statuses (D-027).</summary>
public class LotTests
{
    private static readonly Guid Kg = Guid.CreateVersion7();
    private static readonly Guid Stores = Guid.CreateVersion7();
    private static readonly Guid Hopper = Guid.CreateVersion7();

    private static Lot Received(decimal quantity, bool bulk = false) =>
        Lot.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), bulk ? "@RM" : "R-1", Kg, Stores, bulk, LotOrigin.Received)
            .Add(quantity, LotEventKind.Received).Lot;

    [Fact]
    public void Adding_stock_records_the_quantity_after_it()
    {
        var (lot, movement) = Lot.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "R-1", Kg, Stores, false, LotOrigin.Received)
            .Add(1200m, LotEventKind.Received);

        Assert.Equal((1200m, LotStatus.Available), (lot.Quantity, lot.Status));
        Assert.Equal(new LotMovement(LotEventKind.Received, 1200m, 1200m, ToLocationId: Stores), movement);
        Assert.Throws<DomainRuleException>(() => lot.Add(0m, LotEventKind.Adjusted));
    }

    [Fact]
    public void Consuming_to_zero_uses_the_lot_up_and_a_write_up_brings_it_back()
    {
        var lot = Received(100m);

        var (partly, first) = lot.Take(40m, LotEventKind.Consumed, "kg");
        Assert.Equal((60m, LotStatus.Available, -40m), (partly.Quantity, partly.Status, first.QtyDelta));

        var (used, last) = partly.Take(60m, LotEventKind.Consumed, "kg");
        Assert.Equal((0m, LotStatus.Consumed), (used.Quantity, used.Status));
        Assert.Equal((LotStatus.Available, LotStatus.Consumed), (last.FromStatus, last.ToStatus));

        var (back, _) = used.Add(5m, LotEventKind.Adjusted);
        Assert.Equal((5m, LotStatus.Available), (back.Quantity, back.Status));
    }

    [Fact]
    public void No_lot_goes_below_zero()
    {
        var ex = Assert.Throws<DomainRuleException>(() => Received(10m).Take(10.5m, LotEventKind.Consumed, "kg"));
        Assert.Contains("only 10 kg", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_held_lot_can_be_written_off_or_moved_but_not_consumed()
    {
        var held = Received(50m).ChangeStatus(LotStatus.OnHold).Lot;

        Assert.Throws<DomainRuleException>(() => held.Take(1m, LotEventKind.Consumed, "kg"));
        var (scrapped, _) = held.Take(50m, LotEventKind.Scrapped, "kg");
        Assert.Equal(LotStatus.Scrapped, scrapped.Status);
        Assert.Equal(Hopper, held.MoveTo(Hopper).Lot.LocationId);
    }

    [Fact]
    public void Status_changes_only_between_the_on_hand_statuses()
    {
        var lot = Received(5m);

        var (held, movement) = lot.ChangeStatus(LotStatus.Quarantine);
        Assert.Equal((LotStatus.Available, LotStatus.Quarantine), (movement.FromStatus, movement.ToStatus));
        Assert.Throws<DomainRuleException>(() => held.ChangeStatus(LotStatus.Quarantine));
        Assert.Throws<DomainRuleException>(() => lot.ChangeStatus(LotStatus.Consumed));

        var used = lot.Take(5m, LotEventKind.Consumed, "kg").Lot;
        Assert.Throws<DomainRuleException>(() => used.ChangeStatus(LotStatus.OnHold));
        Assert.Throws<DomainRuleException>(() => used.MoveTo(Hopper));
    }

    [Fact]
    public void A_bulk_balance_moves_by_quantity_and_stays_available_at_zero()
    {
        var bulk = Received(30m, bulk: true);

        Assert.Throws<DomainRuleException>(() => bulk.MoveTo(Hopper));
        var (empty, outgoing) = bulk.TransferOut(30m, Hopper, "kg");
        Assert.Equal((0m, LotStatus.Available), (empty.Quantity, empty.Status));
        Assert.Equal((Stores, Hopper, -30m), (outgoing.FromLocationId, outgoing.ToLocationId, outgoing.QtyDelta));

        Assert.Throws<DomainRuleException>(() => Received(30m).TransferOut(1m, Hopper, "kg"));
        Assert.Throws<DomainRuleException>(() => bulk.TransferOut(1m, Stores, "kg"));
    }

    [Fact]
    public void Generated_numbers_follow_the_job_and_bulk_numbers_the_location()
    {
        Assert.Equal("WO-1001-10-03", Lot.ProducedNumber("WO-1001-10", 3));
        Assert.Equal("WO-1001-10-112", Lot.ProducedNumber("WO-1001-10", 112));
        Assert.Equal("@RM-STORE", Lot.BulkNumber("RM-STORE"));
    }
}
