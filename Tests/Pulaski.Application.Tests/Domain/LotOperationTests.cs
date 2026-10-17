using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Split, merge, ship, correct and reverse (D-030).</summary>
public class LotOperationTests
{
    private static readonly Guid Kg = Guid.CreateVersion7();
    private static readonly Guid Site = Guid.CreateVersion7();
    private static readonly Guid Resin = Guid.CreateVersion7();
    private static readonly Guid Stores = Guid.CreateVersion7();
    private static readonly Guid Hopper = Guid.CreateVersion7();
    private static readonly DateTimeOffset June = new(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static Lot Lot(string number, decimal quantity, DateTimeOffset? expires = null, string status = LotStatus.Available) =>
        new(Guid.CreateVersion7(), Site, Resin, number, status, quantity, Kg, Stores, false, LotOrigin.Received, "SUP-1", expires);

    [Fact]
    public void A_split_keeps_the_lots_details_and_must_leave_some_behind()
    {
        var lot = Lot("R-1", 100m, June, LotStatus.OnHold);

        var (source, outgoing, part, incoming) = lot.Split(30m, Guid.CreateVersion7(), "R-1-1", Hopper, "kg");

        Assert.Equal(70m, source.Quantity);
        Assert.Equal(("R-1-1", 30m, LotStatus.OnHold, "SUP-1", June, Hopper, LotOrigin.Split),
            (part.LotNumber, part.Quantity, part.Status, part.SupplierLot, part.ExpiresAt, part.LocationId, part.Origin));
        Assert.Equal((LotEventKind.Split, -30m, 70m), (outgoing.Kind, outgoing.QtyDelta, outgoing.QtyAfter));
        Assert.Equal((30m, 30m), (incoming.QtyDelta!.Value, incoming.QtyAfter));
        Assert.Throws<DomainRuleException>(() => lot.Split(100m, Guid.CreateVersion7(), "R-1-2", Stores, "kg"));
    }

    [Fact]
    public void A_merge_needs_one_material_location_and_status_and_keeps_the_earliest_expiry()
    {
        var target = Lot("R-1", 100m, June);
        var source = Lot("R-2", 40m, June.AddMonths(-2));

        var (merged, incoming, emptied, outgoing) = target.Absorb(source);

        Assert.Equal((140m, June.AddMonths(-2)), (merged.Quantity, merged.ExpiresAt));
        Assert.Equal((0m, LotStatus.Consumed), (emptied.Quantity, emptied.Status));
        Assert.Equal((LotEventKind.Merged, 40m, -40m), (incoming.Kind, incoming.QtyDelta!.Value, outgoing.QtyDelta!.Value));

        Assert.Throws<DomainRuleException>(() => target.Absorb(Lot("R-3", 5m, status: LotStatus.OnHold)));
        Assert.Throws<DomainRuleException>(() => target.Absorb(Lot("R-4", 5m) with { LocationId = Hopper }));
        Assert.Throws<DomainRuleException>(() => target.Absorb(Lot("R-5", 5m) with { MaterialId = Guid.CreateVersion7() }));
        Assert.Throws<DomainRuleException>(() => target.Absorb(target));
    }

    [Fact]
    public void Shipping_takes_available_stock_and_ships_the_lot_at_zero()
    {
        var (shipped, movement) = Lot("P-1", 400m).Take(400m, LotEventKind.Shipped, "m");

        Assert.Equal((0m, LotStatus.Shipped, LotEventKind.Shipped), (shipped.Quantity, shipped.Status, movement.Kind));
        Assert.Throws<DomainRuleException>(() => Lot("P-2", 5m, status: LotStatus.Quarantine).Take(1m, LotEventKind.Shipped, "m"));
    }

    [Fact]
    public void Correcting_changes_details_but_not_a_balances_number()
    {
        var (corrected, movement) = Lot("R-1", 10m).Correct("R-01", "SUP-2", June);

        Assert.Equal(("R-01", "SUP-2", June, 10m), (corrected.LotNumber, corrected.SupplierLot, corrected.ExpiresAt, movement.QtyAfter));
        Assert.Throws<DomainRuleException>(() => corrected.Correct("R-01", "SUP-2", June));
        var bulk = Lot("@RM", 5m) with { IsBulk = true };
        Assert.Throws<DomainRuleException>(() => bulk.Correct("@HOP", null, null));
    }

    [Fact]
    public void Reversing_returns_quantity_location_and_status_and_a_reversed_receipt_cancels_the_lot()
    {
        var empty = Lot("R-1", 0m);
        var (received, receipt) = empty.Add(50m, LotEventKind.Received);
        var (cancelled, undo) = received.Reverse(receipt);
        Assert.Equal((0m, LotStatus.Cancelled), (cancelled.Quantity, cancelled.Status));
        Assert.Equal((LotEventKind.Reversed, -50m, LotStatus.Cancelled), (undo.Kind, undo.QtyDelta!.Value, undo.ToStatus));

        var (scrapped, writeOff) = received.Take(50m, LotEventKind.Scrapped, "kg");
        var (restored, _) = scrapped.Reverse(writeOff);
        Assert.Equal((50m, LotStatus.Available), (restored.Quantity, restored.Status));

        var (moved, move) = received.MoveTo(Hopper);
        var (back, backMove) = moved.Reverse(move);
        Assert.Equal((Stores, Hopper, Stores), (back.LocationId, backMove.FromLocationId, backMove.ToLocationId));

        var (held, hold) = received.ChangeStatus(LotStatus.OnHold);
        Assert.Equal(LotStatus.Available, held.Reverse(hold).Lot.Status);

        Assert.Throws<DomainRuleException>(() => cancelled.Reverse(undo));
        var (used, consumption) = received.Take(5m, LotEventKind.Consumed, "kg");
        Assert.Throws<DomainRuleException>(() => used.Reverse(consumption));
    }

    [Fact]
    public void Posting_a_count_adjusts_what_differs_and_refuses_uncounted_or_changed_lots()
    {
        var counted = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        var a = Lot("A", 100m);
        var b = Lot("B", 20m);
        var c = Lot("C", 7m);
        var count = Guid.CreateVersion7();
        StockCountLine Line(Lot lot, decimal? qty) => new(Guid.CreateVersion7(), count, lot.Id, lot.Quantity, qty, qty is null ? null : counted, "test");
        var lots = new Dictionary<Guid, CountedLot>
        {
            [a.Id] = new(a, counted.AddHours(-1)),
            [b.Id] = new(b, counted.AddHours(-1)),
            [c.Id] = new(c, counted.AddHours(1)),
        };

        var (adjustments, problems) = StockCountPosting.Plan([Line(a, 96m), Line(b, 20m)], lots);
        Assert.Empty(problems);
        Assert.Equal((a.Id, -4m), (Assert.Single(adjustments).Lot.Id, adjustments[0].Variance));

        var (_, refused) = StockCountPosting.Plan([Line(a, null), Line(c, 7m)], lots);
        Assert.Equal(2, refused.Count);
        Assert.Contains(refused, p => p.Contains("isn't counted", StringComparison.Ordinal));
        Assert.Contains(refused, p => p.Contains("changed after", StringComparison.Ordinal));
        Assert.Equal("CNT-20261016-3", StockCount.NumberFor(counted, 3));
    }
}
