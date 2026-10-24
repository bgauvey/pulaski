using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.Units;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>One material at a time, stock-holding locations, shelf life and expiry (D-032).</summary>
public class LocationRuleHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Unit _kg;
    private readonly InventoryLocation _stores;
    private readonly InventoryLocation _silo;
    private readonly InventoryMaterial _resin;
    private readonly InventoryMaterial _blend;
    private readonly InventoryReason _count;

    public LocationRuleHandlerTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _stores = Inventory.AddLocation("RM-STORE", _site);
        _silo = Inventory.AddLocation("SILO-1", _site, comminglesLots: true, singleMaterial: true);
        _resin = Inventory.AddMaterial("PVC-K67", _kg.Id, "kg", shelfLifeDays: 180);
        _blend = Inventory.AddMaterial("BLEND-1", _kg.Id, "kg");
        _count = Inventory.AddReason(ReasonKinds.InventoryAdjustment, "COUNT");
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private Task<LotTransactionResponse> Receive(Guid material, string? number, decimal qty, Guid location, DateTimeOffset? expires = null) =>
        new ReceiveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ReceiveLotCommand(material, location, number, qty, _kg.Id, ExpiresAt: expires), CancellationToken.None);

    [Fact]
    public async Task A_one_material_location_refuses_another_material_until_it_is_empty()
    {
        var resin = await Receive(_resin.Id, null, 100m, _silo.Id);
        var blend = await Receive(_blend.Id, "B-1", 50m, _stores.Id);
        var move = new MoveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Receive(_blend.Id, "B-2", 10m, _silo.Id));
        Assert.Contains("holds PVC-K67", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            move.HandleAsync(new MoveLotCommand(blend.LotId, blend.Version, _silo.Id), CancellationToken.None));

        // The same material is welcome, and once the silo is empty anything can go in.
        await Receive(_resin.Id, "R-2", 20m, _silo.Id);
        var balance = Inventory.Find(resin.LotId)!;
        await new WriteOffLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new WriteOffLotCommand(resin.LotId, balance.Version, null, null, _count.Id), CancellationToken.None);
        var moved = await move.HandleAsync(new MoveLotCommand(blend.LotId, Inventory.Find(blend.LotId)!.Version, _silo.Id), CancellationToken.None);
        Assert.Equal(50m, moved.Quantity);
    }

    [Fact]
    public async Task Shelf_life_sets_a_new_lots_expiry_unless_one_is_given()
    {
        var lot = await Receive(_resin.Id, "R-1", 10m, _stores.Id);
        Assert.Equal(HandlerFixture.Now.AddDays(180), Inventory.Find(lot.LotId)!.Value.ExpiresAt);

        var given = await Receive(_resin.Id, "R-2", 10m, _stores.Id, HandlerFixture.Now.AddDays(30));
        Assert.Equal(HandlerFixture.Now.AddDays(30), Inventory.Find(given.LotId)!.Value.ExpiresAt);

        var none = await Receive(_blend.Id, "B-1", 10m, _stores.Id);
        Assert.Null(Inventory.Find(none.LotId)!.Value.ExpiresAt);
    }

    [Fact]
    public async Task An_expired_lot_cant_be_consumed_shipped_or_merged_until_its_expiry_is_corrected()
    {
        var job = Inventory.AddJob("WO-1-10", _site, _blend.Id, status: "running");
        _f.Stores.Execution.OpenRun(job.Id, job.EquipmentId, HandlerFixture.Now.AddHours(-1));
        var lot = await Receive(_resin.Id, "R-1", 10m, _stores.Id, HandlerFixture.Now.AddDays(1));
        var other = await Receive(_resin.Id, "R-2", 10m, _stores.Id, HandlerFixture.Now.AddDays(1));
        _f.Time.Advance(TimeSpan.FromDays(2));

        var consume = new ConsumeLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Execution, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            consume.HandleAsync(new ConsumeLotCommand(lot.LotId, job.Id, 1m, _kg.Id), CancellationToken.None));
        Assert.Contains("correct its expiry", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            new ShipLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
                .HandleAsync(new ShipLotCommand(lot.LotId, lot.Version, null, null, "DN-1"), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            new MergeLotsHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
                .HandleAsync(new MergeLotsCommand(lot.LotId, lot.Version, [other.LotId]), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            new MoveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
                .HandleAsync(new MoveLotCommand(other.LotId, other.Version, _silo.Id), CancellationToken.None));

        await new CorrectLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CorrectLotCommand(lot.LotId, lot.Version, "R-1", null, _f.Time.GetUtcNow().AddDays(30), "QA extended after retest"), CancellationToken.None);
        var used = await consume.HandleAsync(new ConsumeLotCommand(lot.LotId, job.Id, 1m, _kg.Id), CancellationToken.None);
        Assert.Equal(9m, used.Quantity);
    }

    [Fact]
    public async Task Shelf_life_is_set_with_the_materials_version()
    {
        var set = new SetShelfLifeHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events);

        var changed = await set.HandleAsync(new SetShelfLifeCommand(_blend.Id, 1, 90), CancellationToken.None);
        Assert.Equal(90, Inventory.FindMaterial(_blend.Id)!.ShelfLifeDays);
        await Assert.ThrowsAsync<Errors.ConflictException>(() => set.HandleAsync(new SetShelfLifeCommand(_blend.Id, 1, 30), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => set.HandleAsync(new SetShelfLifeCommand(_blend.Id, changed.Version, 0), CancellationToken.None));
        await set.HandleAsync(new SetShelfLifeCommand(_blend.Id, changed.Version, null), CancellationToken.None);
        Assert.Null(Inventory.FindMaterial(_blend.Id)!.ShelfLifeDays);
    }

    [Fact]
    public async Task A_location_holding_stock_cant_be_closed_and_two_materials_block_one_material()
    {
        var equipment = _f.Stores.Equipment;
        var (_, site, _) = equipment.AddHierarchy();
        var bin = Equipment.Create(Guid.CreateVersion7(), site, "BIN-1", "Bin", null, "work_center", "storage_zone", false, false, true, null);
        equipment.Put(bin, version: 1);
        var update = new UpdateEquipmentHandler(_f.Stores.UnitOfWork, equipment, _f.Stores.Events, _f.Events);
        UpdateEquipmentCommand Change(bool holds = true, bool active = true, bool? single = null) =>
            new(bin.Id, equipment.Find(bin.Id)!.Version, "Bin", null, false, false, holds, active, SingleMaterial: single);
        equipment.MaterialsHeld[bin.Id] = 2;

        await Assert.ThrowsAsync<DomainRuleException>(() => update.HandleAsync(Change(active: false), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => update.HandleAsync(Change(holds: false), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => update.HandleAsync(Change(single: true), CancellationToken.None));

        equipment.MaterialsHeld[bin.Id] = 1;
        await update.HandleAsync(Change(single: true), CancellationToken.None);
        Assert.True(equipment.Find(bin.Id)!.Value.SingleMaterial);

        equipment.MaterialsHeld.Clear();
        await update.HandleAsync(Change(active: false), CancellationToken.None);
        Assert.False(equipment.Find(bin.Id)!.Value.IsActive);
    }
}
