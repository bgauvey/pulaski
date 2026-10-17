using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Units;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Receiving, adjusting, moving, holding, consuming and producing lots (D-027).</summary>
public class InventoryHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Unit _kg;
    private readonly Unit _lb;
    private readonly Unit _m;
    private readonly Unit _ea;
    private readonly InventoryLocation _stores;
    private readonly InventoryLocation _hopper;
    private readonly InventoryMaterial _resin;
    private readonly InventoryMaterial _profile;
    private readonly InventoryMaterial _caps;
    private readonly InventoryReason _count;
    private readonly InventoryReason _other;
    private readonly InventoryReason _qaHold;
    private readonly InventoryJob _job;

    public InventoryHandlerTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _lb = _f.Stores.Units.AddUnit("lb", "mass", 0.45359237m);
        _m = _f.Stores.Units.AddUnit("m", "length", 1m);
        _ea = _f.Stores.Units.AddUnit("ea", "count", 1m);
        _stores = Inventory.AddLocation("RM-STORE", _site);
        _hopper = Inventory.AddLocation("EXT-01-HOP", _site);
        _resin = Inventory.AddMaterial("PVC-K67", _kg.Id, "kg");
        _profile = Inventory.AddMaterial("PRF-4410-WH", _m.Id, "m");
        _caps = Inventory.AddMaterial("CAP-4410", _ea.Id, "ea", isLotTracked: false);
        _count = Inventory.AddReason(ReasonKinds.InventoryAdjustment, "COUNT");
        _other = Inventory.AddReason(ReasonKinds.InventoryAdjustment, "OTHER", requiresComment: true);
        _qaHold = Inventory.AddReason(ReasonKinds.Hold, "QA-HOLD");
        _job = Inventory.AddJob("WO-1001-10", _site, _profile.Id);
        Inventory.PlanComponent(_job.Id, _resin.Id);
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private Task<LotTransactionResponseOf> Receive(string? lot, decimal qty, Guid? uom = null, Guid? material = null, Guid? location = null) =>
        Run(new ReceiveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new ReceiveLotCommand(material ?? _resin.Id, location ?? _stores.Id, lot, qty, uom ?? _kg.Id), CancellationToken.None));

    private Task<LotTransactionResponseOf> Consume(Guid lotId, decimal qty, Guid? job = null) =>
        Run(new ConsumeLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new ConsumeLotCommand(lotId, job ?? _job.Id, qty, _kg.Id), CancellationToken.None));

    private Task<LotTransactionResponseOf> Produce(decimal qty, string? lot = null, Guid? location = null) =>
        Run(new ProduceLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new ProduceLotCommand(_job.Id, location ?? _stores.Id, qty, _m.Id, lot), CancellationToken.None));

    private static async Task<LotTransactionResponseOf> Run(Task<Contracts.Inventory.LotTransactionResponse> task) => new(await task);

    private long VersionOf(Guid lotId) => Inventory.Find(lotId)!.Version;

    [Fact]
    public async Task Receiving_converts_to_the_base_unit_and_records_the_movement()
    {
        var received = await Receive(" R-24-0917 ", 2500m, _lb.Id);

        var lot = Inventory.Find(received.Value.LotId)!.Value;
        Assert.Equal(("R-24-0917", 1133.980925m, _kg.Id, LotStatus.Available, _site), (lot.LotNumber, lot.Quantity, lot.UomId, lot.Status, lot.SiteId));
        var row = Assert.Single(Inventory.Events);
        Assert.Equal((LotEventKind.Received, 1133.980925m, 1133.980925m, "test-user"), (row.Movement.Kind, row.Movement.QtyDelta, row.Movement.QtyAfter, row.ActorId));
        Assert.Equal(EventTypes.LotReceived, Assert.Single(_f.Stores.Events.Appended).Type);
    }

    [Fact]
    public async Task Receiving_refuses_a_duplicate_number_a_missing_one_and_a_unit_that_doesnt_convert()
    {
        await Receive("R-1", 10m);

        await Assert.ThrowsAsync<ConflictException>(() => Receive("R-1", 5m));
        var missing = await Assert.ThrowsAsync<ValidationException>(() => Receive(null, 5m));
        Assert.Contains("LotNumber", missing.Errors.Keys);
        await Assert.ThrowsAsync<DomainRuleException>(() => Receive("R-2", 5m, _m.Id));
    }

    [Fact]
    public async Task Receiving_only_goes_to_an_active_location_that_holds_inventory()
    {
        var line = Inventory.AddLocation("EXT-01", _site, holdsInventory: false);
        var closed = Inventory.AddLocation("OLD-STORE", _site, isActive: false);

        await Assert.ThrowsAsync<DomainRuleException>(() => Receive("R-1", 5m, location: line.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => Receive("R-1", 5m, location: closed.Id));
    }

    [Fact]
    public async Task A_material_that_isnt_lot_tracked_adds_to_one_balance_per_location()
    {
        var first = await Receive(null, 500m, _ea.Id, _caps.Id);
        var second = await Receive(null, 250m, _ea.Id, _caps.Id);

        Assert.Equal(first.Value.LotId, second.Value.LotId);
        var bulk = Inventory.Find(first.Value.LotId)!.Value;
        Assert.Equal(((string?)null, true, 750m), (bulk.LotNumber, bulk.IsBulk, bulk.Quantity));
        await Assert.ThrowsAsync<ValidationException>(() => Receive("C-1", 1m, _ea.Id, _caps.Id));
    }

    [Fact]
    public async Task Write_up_and_write_off_need_a_reason_and_check_the_version()
    {
        var lot = (await Receive("R-1", 100m)).Value;
        var writeUp = new WriteUpLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids);
        var writeOff = new WriteOffLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids);

        var up = await writeUp.HandleAsync(new WriteUpLotCommand(lot.LotId, lot.Version, 5m, _kg.Id, _count.Id), CancellationToken.None);
        Assert.Equal(105m, up.Quantity);
        await Assert.ThrowsAsync<ConflictException>(() =>
            writeUp.HandleAsync(new WriteUpLotCommand(lot.LotId, lot.Version, 5m, _kg.Id, _count.Id), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            writeUp.HandleAsync(new WriteUpLotCommand(lot.LotId, up.Version, 5m, _kg.Id, _qaHold.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            writeOff.HandleAsync(new WriteOffLotCommand(lot.LotId, up.Version, 5m, _kg.Id, _other.Id), CancellationToken.None));

        var off = await writeOff.HandleAsync(new WriteOffLotCommand(lot.LotId, up.Version, null, null, _other.Id, "Water damage"), CancellationToken.None);
        Assert.Equal((0m, LotStatus.Scrapped), (off.Quantity, off.Status));
        var last = Inventory.Events[^1];
        Assert.Equal((LotEventKind.Scrapped, -105m, _other.Id, "Water damage"), (last.Movement.Kind, last.Movement.QtyDelta, last.ReasonCodeId, last.Comment));
    }

    [Fact]
    public async Task A_lot_moves_whole_within_its_site()
    {
        var lot = (await Receive("R-1", 100m)).Value;
        var move = new MoveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids);
        var elsewhere = Inventory.AddLocation("OTHER-SITE", Guid.CreateVersion7());

        await move.HandleAsync(new MoveLotCommand(lot.LotId, lot.Version, _hopper.Id), CancellationToken.None);

        Assert.Equal(_hopper.Id, Inventory.Find(lot.LotId)!.Value.LocationId);
        Assert.Equal((_stores.Id, _hopper.Id), (Inventory.Events[^1].Movement.FromLocationId, Inventory.Events[^1].Movement.ToLocationId));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            move.HandleAsync(new MoveLotCommand(lot.LotId, VersionOf(lot.LotId), elsewhere.Id), CancellationToken.None));
    }

    [Fact]
    public async Task A_bulk_balance_transfers_a_quantity_to_the_other_locations_balance()
    {
        var bulk = (await Receive(null, 500m, _ea.Id, _caps.Id)).Value;
        var move = new MoveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids);

        await Assert.ThrowsAsync<ValidationException>(() =>
            move.HandleAsync(new MoveLotCommand(bulk.LotId, bulk.Version, _hopper.Id), CancellationToken.None));
        var sent = await move.HandleAsync(new MoveLotCommand(bulk.LotId, bulk.Version, _hopper.Id, 120m, _ea.Id), CancellationToken.None);

        Assert.Equal(380m, sent.Quantity);
        var target = Assert.Single(Inventory.Lots, l => l.LocationId == _hopper.Id);
        Assert.Equal(((string?)null, 120m, true), (target.LotNumber, target.Quantity, target.IsBulk));
        var (outgoing, incoming) = (Inventory.Events[^2], Inventory.Events[^1]);
        Assert.Equal((target.Id, bulk.LotId), (outgoing.RelatedLotId, incoming.RelatedLotId));
        Assert.Equal(2, _f.Stores.Events.Appended.Count(e => e.Type == EventTypes.LotMoved));
    }

    [Fact]
    public async Task A_hold_needs_a_hold_reason_and_a_held_lot_cant_be_consumed()
    {
        var lot = (await Receive("R-1", 100m)).Value;
        var status = new ChangeLotStatusHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids);

        await Assert.ThrowsAsync<ValidationException>(() =>
            status.HandleAsync(new ChangeLotStatusCommand(lot.LotId, lot.Version, LotStatus.OnHold), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            status.HandleAsync(new ChangeLotStatusCommand(lot.LotId, lot.Version, LotStatus.OnHold, _count.Id), CancellationToken.None));
        var held = await status.HandleAsync(new ChangeLotStatusCommand(lot.LotId, lot.Version, LotStatus.OnHold, _qaHold.Id), CancellationToken.None);
        Assert.Equal(LotStatus.OnHold, held.Status);

        await Assert.ThrowsAsync<DomainRuleException>(() => Consume(lot.LotId, 10m));
        var released = await status.HandleAsync(new ChangeLotStatusCommand(lot.LotId, held.Version, LotStatus.Available), CancellationToken.None);
        Assert.Equal(LotStatus.Available, released.Status);
    }

    [Fact]
    public async Task Consumption_and_production_link_every_input_of_the_job_to_every_output()
    {
        var a = (await Receive("R-A", 100m)).Value;
        var b = (await Receive("R-B", 100m)).Value;

        await Consume(a.LotId, 60m);
        var first = (await Produce(400m)).Value;
        await Consume(b.LotId, 30m);
        var second = (await Produce(350m)).Value;
        await Consume(a.LotId, 10m);

        Assert.Equal(("WO-1001-10-01", "WO-1001-10-02"), (first.LotNumber, second.LotNumber));
        var edges = Inventory.Edges.Select(e => (e.ParentLotId, e.ChildLotId)).ToHashSet();
        Assert.Equal(
            new HashSet<(Guid, Guid)> { (a.LotId, first.LotId), (b.LotId, first.LotId), (a.LotId, second.LotId), (b.LotId, second.LotId) },
            edges);
        Assert.All(Inventory.Edges, e => Assert.Equal(_job.Id, e.JobId));
        Assert.Equal(30m, Inventory.Find(a.LotId)!.Value.Quantity);
        Assert.Equal(_job.Id, Inventory.Find(first.LotId)!.Value.ProducedByJobId);
    }

    [Fact]
    public async Task Consuming_an_unplanned_material_warns_and_overdrawing_is_refused()
    {
        var other = Inventory.AddMaterial("TIO2-MB", _kg.Id, "kg");
        var lot = (await Receive("M-1", 20m, material: other.Id)).Value;

        var consumed = await Consume(lot.LotId, 5m);
        Assert.Equal("not_planned", Assert.Single(consumed.Value.Warnings).Code);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Consume(lot.LotId, 50m));
        Assert.Contains("only 15 kg", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declarations_need_an_open_production_job_in_the_lots_site()
    {
        var lot = (await Receive("R-1", 100m)).Value;
        var closed = Inventory.AddJob("WO-0999-10", _site, _profile.Id, status: "closed");
        var cleaning = Inventory.AddJob("CLN-1", _site, null, jobType: "cleaning");
        var elsewhere = Inventory.AddJob("WO-2000-10", Guid.CreateVersion7(), _profile.Id);

        await Assert.ThrowsAsync<DomainRuleException>(() => Consume(lot.LotId, 1m, closed.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => Consume(lot.LotId, 1m, cleaning.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => Consume(lot.LotId, 1m, elsewhere.Id));
    }

    [Fact]
    public async Task Producing_into_a_named_lot_adds_only_to_this_jobs_lot_at_that_location()
    {
        var made = (await Produce(100m, "P-1")).Value;
        var more = (await Produce(50m, "P-1")).Value;
        Assert.Equal((made.LotId, 150m), (more.LotId, more.Quantity));

        await Assert.ThrowsAsync<ConflictException>(() => Produce(10m, "P-1", _hopper.Id));
        await Receive("R-9", 5m, _m.Id, _profile.Id);
        await Assert.ThrowsAsync<ConflictException>(() => Produce(10m, "R-9"));

        // The generated number skips one already taken by hand.
        await Receive("WO-1001-10-02", 5m, _m.Id, _profile.Id);
        Assert.Equal("WO-1001-10-03", (await Produce(10m)).Value.LotNumber);
    }

    /// <summary>Wraps the response so tests read <c>.Value</c> the same way for every command.</summary>
    private sealed record LotTransactionResponseOf(Contracts.Inventory.LotTransactionResponse Value);
}
