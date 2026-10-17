using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Units;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Locations whose lots are indistinguishable, like a silo (D-031).</summary>
public class ComminglingHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Unit _kg;
    private readonly InventoryLocation _stores;
    private readonly InventoryLocation _silo;
    private readonly InventoryMaterial _resin;

    public ComminglingHandlerTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _stores = Inventory.AddLocation("RM-STORE", _site);
        _silo = Inventory.AddLocation("SILO-1", _site, comminglesLots: true);
        _resin = Inventory.AddMaterial("PVC-K67", _kg.Id, "kg");
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private Task<LotTransactionResponse> Receive(string? number, decimal qty, Guid location) =>
        new ReceiveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ReceiveLotCommand(_resin.Id, location, number, qty, _kg.Id), CancellationToken.None);

    private Lot Balance() => Assert.Single(Inventory.Lots, l => l.IsBulk && l.LocationId == _silo.Id);

    [Fact]
    public async Task Receiving_a_numbered_lot_keeps_it_for_genealogy_and_merges_it_into_the_balance()
    {
        var first = await Receive("R-1", 600m, _silo.Id);
        var second = await Receive("R-2", 400m, _silo.Id);

        var balance = Balance();
        Assert.Equal((balance.Id, (string?)null, 1000m), (second.LotId, balance.LotNumber, balance.Quantity));
        Assert.Equal("commingled", Assert.Single(second.Warnings).Code);
        Assert.All(Inventory.Lots.Where(l => !l.IsBulk), l => Assert.Equal((0m, LotStatus.Consumed), (l.Quantity, l.Status)));
        Assert.Equal(2, Inventory.Edges.Count(e => e.ChildLotId == balance.Id && e.Basis == GenealogyEdge.Merge));
        Assert.Equal(first.LotId, second.LotId);
    }

    [Fact]
    public async Task A_receipt_with_no_number_goes_straight_into_the_balance_but_only_where_lots_mix()
    {
        await Receive(null, 250m, _silo.Id);

        Assert.Equal(250m, Balance().Quantity);
        Assert.DoesNotContain(Inventory.Lots, l => !l.IsBulk);
        await Assert.ThrowsAsync<Errors.ValidationException>(() => Receive(null, 1m, _stores.Id));
    }

    [Fact]
    public async Task Moving_or_splitting_a_lot_into_the_silo_merges_it_but_a_held_lot_must_be_released_first()
    {
        await Receive(null, 100m, _silo.Id);
        var lot = await Receive("R-5", 80m, _stores.Id);
        var move = new MoveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        var split = new SplitLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

        var part = await split.HandleAsync(new SplitLotCommand(lot.LotId, lot.Version, 30m, _kg.Id, ToLocationId: _silo.Id), CancellationToken.None);
        Assert.Equal((Balance().Id, 130m), (part.LotId, part.Quantity));

        var moved = await move.HandleAsync(new MoveLotCommand(lot.LotId, Inventory.Find(lot.LotId)!.Version, _silo.Id), CancellationToken.None);
        Assert.Equal(180m, moved.Quantity);
        Assert.Equal((0m, LotStatus.Consumed), (Inventory.Find(lot.LotId)!.Value.Quantity, Inventory.Find(lot.LotId)!.Value.Status));

        var held = await Receive("R-6", 10m, _stores.Id);
        var hold = Inventory.AddReason(ReasonKinds.Hold, "QA");
        var onHold = await new ChangeLotStatusHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new ChangeLotStatusCommand(held.LotId, held.Version, LotStatus.OnHold, hold.Id), CancellationToken.None);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            move.HandleAsync(new MoveLotCommand(held.LotId, onHold.Version, _silo.Id), CancellationToken.None));
    }

    [Fact]
    public void Lots_can_only_mix_where_inventory_is_held()
    {
        Assert.Throws<DomainRuleException>(() => Equipment.EnsureStorageFlags(holdsInventory: false, comminglesLots: true, singleMaterial: false));
        Equipment.EnsureStorageFlags(holdsInventory: true, comminglesLots: true, singleMaterial: true);
    }

    [Fact]
    public async Task Mixing_cant_be_turned_on_where_separate_lots_sit()
    {
        var equipment = _f.Stores.Equipment;
        var (_, site, _) = equipment.AddHierarchy();
        var bin = Equipment.Create(Guid.CreateVersion7(), site, "BIN-1", "Bin", null, "work_center", "storage_zone", false, false, true, null);
        equipment.Put(bin, version: 1);
        var store = equipment.Find(bin.Id)!;
        var update = new UpdateEquipmentHandler(_f.Stores.UnitOfWork, equipment, _f.Stores.Events, _f.Events);
        equipment.WithSeparateLots.Add(store.Value.Id);

        await Assert.ThrowsAsync<DomainRuleException>(() => update.HandleAsync(
            new UpdateEquipmentCommand(store.Value.Id, store.Version, "Bin", null, false, false, true, true, ComminglesLots: true), CancellationToken.None));

        equipment.WithSeparateLots.Clear();
        await update.HandleAsync(new UpdateEquipmentCommand(store.Value.Id, store.Version, "Bin", null, false, false, true, true, ComminglesLots: true), CancellationToken.None);
        Assert.True(equipment.Find(store.Value.Id)!.Value.ComminglesLots);
    }
}
