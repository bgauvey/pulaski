using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Units;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Split, merge, ship, correct, reverse, counts and reason codes (D-030).</summary>
public class LotOperationHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Unit _kg;
    private readonly InventoryLocation _stores;
    private readonly InventoryLocation _hopper;
    private readonly InventoryMaterial _resin;
    private readonly InventoryReason _count;

    public LotOperationHandlerTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _stores = Inventory.AddLocation("RM-STORE", _site);
        _hopper = Inventory.AddLocation("EXT-01-HOP", _site);
        _resin = Inventory.AddMaterial("PVC-K67", _kg.Id, "kg");
        _count = Inventory.AddReason(ReasonKinds.InventoryAdjustment, "COUNT");
        Inventory.AddSite(_site);
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private async Task<LotTransactionResponse> Receive(string number, decimal qty, Guid? location = null) =>
        await new ReceiveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ReceiveLotCommand(_resin.Id, location ?? _stores.Id, number, qty, _kg.Id), CancellationToken.None);

    private long VersionOf(Guid lotId) => Inventory.Find(lotId)!.Version;

    [Fact]
    public async Task A_split_numbers_the_new_lot_after_the_old_one_and_links_them()
    {
        var lot = await Receive("R-1", 100m);
        await Receive("R-1-1", 1m);
        var split = new SplitLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

        var part = await split.HandleAsync(new SplitLotCommand(lot.LotId, lot.Version, 25m, _kg.Id, ToLocationId: _hopper.Id), CancellationToken.None);

        Assert.Equal(("R-1-2", 25m), (part.LotNumber, part.Quantity));
        Assert.Equal(75m, Inventory.Find(lot.LotId)!.Value.Quantity);
        var edge = Assert.Single(Inventory.Edges);
        Assert.Equal((lot.LotId, part.LotId, GenealogyEdge.Split, (Guid?)null), (edge.ParentLotId, edge.ChildLotId, edge.Basis, edge.JobId));
        await Assert.ThrowsAsync<ConflictException>(() => split.HandleAsync(
            new SplitLotCommand(lot.LotId, VersionOf(lot.LotId), 5m, _kg.Id, NewLotNumber: "R-1-1"), CancellationToken.None));
    }

    [Fact]
    public async Task A_merge_empties_the_sources_into_the_target()
    {
        var target = await Receive("R-1", 100m);
        var a = await Receive("R-2", 10m);
        var b = await Receive("R-3", 5m);
        var elsewhere = await Receive("R-4", 5m, _hopper.Id);
        var merge = new MergeLotsHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

        await Assert.ThrowsAsync<DomainRuleException>(() => merge.HandleAsync(
            new MergeLotsCommand(target.LotId, target.Version, [a.LotId, elsewhere.LotId]), CancellationToken.None));
        var merged = await merge.HandleAsync(new MergeLotsCommand(target.LotId, target.Version, [b.LotId, a.LotId]), CancellationToken.None);

        Assert.Equal(115m, merged.Quantity);
        Assert.All([a.LotId, b.LotId], id => Assert.Equal((0m, LotStatus.Consumed), (Inventory.Find(id)!.Value.Quantity, Inventory.Find(id)!.Value.Status)));
        Assert.Equal(2, Inventory.Edges.Count(e => e.ChildLotId == target.LotId && e.Basis == GenealogyEdge.Merge));
        await Assert.ThrowsAsync<ValidationException>(() => merge.HandleAsync(
            new MergeLotsCommand(target.LotId, merged.Version, [target.LotId]), CancellationToken.None));
    }

    [Fact]
    public async Task Shipping_needs_a_reference_and_records_it()
    {
        var lot = await Receive("P-1", 40m);
        var ship = new ShipLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

        await Assert.ThrowsAsync<ValidationException>(() => ship.HandleAsync(new ShipLotCommand(lot.LotId, lot.Version, null, null, " "), CancellationToken.None));
        var shipped = await ship.HandleAsync(new ShipLotCommand(lot.LotId, lot.Version, null, null, "DN-4471"), CancellationToken.None);

        Assert.Equal((0m, LotStatus.Shipped), (shipped.Quantity, shipped.Status));
        Assert.Equal((LotEventKind.Shipped, "DN-4471"), (Inventory.Events[^1].Movement.Kind, Inventory.Events[^1].Reference));
    }

    [Fact]
    public async Task A_correction_needs_a_comment_and_a_free_number()
    {
        var lot = await Receive("R-1", 10m);
        await Receive("R-9", 1m);
        var correct = new CorrectLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids);

        await Assert.ThrowsAsync<ValidationException>(() => correct.HandleAsync(new CorrectLotCommand(lot.LotId, lot.Version, "R-01", null, null, ""), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() => correct.HandleAsync(new CorrectLotCommand(lot.LotId, lot.Version, "R-9", null, null, "Typo"), CancellationToken.None));
        var fixedLot = await correct.HandleAsync(new CorrectLotCommand(lot.LotId, lot.Version, "R-01", "SUP-7", null, "Typo on the label"), CancellationToken.None);

        Assert.Equal("R-01", fixedLot.LotNumber);
        Assert.Equal(("SUP-7", LotEventKind.Corrected), (Inventory.Find(lot.LotId)!.Value.SupplierLot, Inventory.Events[^1].Movement.Kind));
    }

    [Fact]
    public async Task Only_the_latest_movement_is_reversed_and_a_reversed_receipt_cancels_the_lot()
    {
        var lot = await Receive("R-1", 10m);
        var reverse = new ReverseMovementHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids);
        var receipt = Inventory.Events[^1];

        var undone = await reverse.HandleAsync(new ReverseMovementCommand(lot.LotId, lot.Version, receipt.Id, _count.Id, "Wrong material"), CancellationToken.None);

        Assert.Equal((0m, LotStatus.Cancelled), (undone.Quantity, undone.Status));
        var row = Inventory.Events[^1];
        Assert.Equal((LotEventKind.Reversed, receipt.Id, _count.Id), (row.Movement.Kind, row.ReversesEventId, row.ReasonCodeId));
        await Assert.ThrowsAsync<DomainRuleException>(() => reverse.HandleAsync(
            new ReverseMovementCommand(lot.LotId, undone.Version, receipt.Id, _count.Id), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => reverse.HandleAsync(
            new ReverseMovementCommand(lot.LotId, undone.Version, row.Id, _count.Id), CancellationToken.None));
    }

    [Fact]
    public async Task A_count_snapshots_the_location_records_counts_and_posts_the_differences()
    {
        var a = await Receive("R-1", 100m);
        var b = await Receive("R-2", 20m);
        await Receive("R-3", 5m, _hopper.Id);

        var started = await new StartStockCountHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new StartStockCountCommand(_stores.Id, "Month end"), CancellationToken.None);
        var count = Inventory.FindCount(started.Id)!.Value;
        Assert.Equal((StockCountStatus.Open, _stores.Id), (count.Status, count.LocationId));
        Assert.StartsWith("CNT-", count.Number, StringComparison.Ordinal);
        var lines = Inventory.LinesOf(count.Id).ToDictionary(l => l.LotId);
        Assert.Equal(new HashSet<Guid> { a.LotId, b.LotId }, lines.Keys.ToHashSet());

        var record = new RecordCountsHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events);
        var post = new PostStockCountHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids);
        var partly = await record.HandleAsync(new RecordCountsCommand(count.Id, started.Version, [new(lines[a.LotId].Id, 97m)]), CancellationToken.None);
        await Assert.ThrowsAsync<DomainRuleException>(() => post.HandleAsync(new PostStockCountCommand(count.Id, partly.Version, _count.Id), CancellationToken.None));

        _f.Time.Advance(TimeSpan.FromMinutes(5));
        var all = await record.HandleAsync(new RecordCountsCommand(count.Id, partly.Version, [new(lines[b.LotId].Id, 22.5m)]), CancellationToken.None);
        var result = await post.HandleAsync(new PostStockCountCommand(count.Id, all.Version, _count.Id), CancellationToken.None);

        Assert.Equal(2, result.Adjusted);
        Assert.Equal((97m, 22.5m), (Inventory.Find(a.LotId)!.Value.Quantity, Inventory.Find(b.LotId)!.Value.Quantity));
        Assert.All(Inventory.Events.TakeLast(2), e => Assert.Equal(count.Number, e.Reference));
        Assert.Equal(StockCountStatus.Posted, Inventory.FindCount(count.Id)!.Value.Status);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            new CancelStockCountHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events)
                .HandleAsync(new CancelStockCountCommand(count.Id, result.Version), CancellationToken.None));
    }

    [Fact]
    public async Task A_lot_that_moved_after_it_was_counted_must_be_recounted()
    {
        var a = await Receive("R-1", 100m);
        var started = await new StartStockCountHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new StartStockCountCommand(_stores.Id), CancellationToken.None);
        var line = Assert.Single(Inventory.LinesOf(started.Id));
        var recorded = await new RecordCountsHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events)
            .HandleAsync(new RecordCountsCommand(started.Id, started.Version, [new(line.Id, 100m)]), CancellationToken.None);
        Inventory.Touch(a.LotId, HandlerFixture.Now.AddMinutes(1));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => new PostStockCountHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new PostStockCountCommand(started.Id, recorded.Version, _count.Id), CancellationToken.None));
        Assert.Contains("count it again", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reason_codes_keep_their_code_and_kind_and_check_the_rest()
    {
        var create = new CreateReasonCodeHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids);
        var created = await create.HandleAsync(new CreateReasonCodeCommand(_site, "inventory_adjustment", " theft ", "Theft", true, 80), CancellationToken.None);

        Assert.Equal("THEFT", Inventory.FindReasonCode(created.Id)!.Value.Code);
        await Assert.ThrowsAsync<ConflictException>(() => create.HandleAsync(new CreateReasonCodeCommand(_site, "inventory_adjustment", "THEFT", "Again", false, 0), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => create.HandleAsync(new CreateReasonCodeCommand(null, "nonsense", "X", "X", false, 0), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => create.HandleAsync(new CreateReasonCodeCommand(null, "hold", "bad code!", "X", false, 0), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => create.HandleAsync(new CreateReasonCodeCommand(Guid.CreateVersion7(), "hold", "X", "X", false, 0), CancellationToken.None));

        var update = new UpdateReasonCodeHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events);
        await update.HandleAsync(new UpdateReasonCodeCommand(created.Id, created.Version, "Theft or loss", false, 85, false), CancellationToken.None);
        var after = Inventory.FindReasonCode(created.Id)!.Value;
        Assert.Equal(("THEFT", "Theft or loss", false, false), (after.Code, after.Name, after.RequiresComment, after.IsActive));
        await Assert.ThrowsAsync<ConflictException>(() => update.HandleAsync(
            new UpdateReasonCodeCommand(created.Id, created.Version, "X", false, 0, true), CancellationToken.None));
    }
}
