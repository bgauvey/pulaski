using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Units;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Containers holding lots, and scanning (D-059): lots keep their identity; moving a container moves them.</summary>
public class ContainerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Unit _kg;
    private readonly InventoryLocation _stores;
    private readonly InventoryLocation _dock;
    private readonly InventoryMaterial _resin;
    private readonly InventoryMaterial _caps;

    public ContainerTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _stores = Inventory.AddLocation("RM-STORE", _site);
        _dock = Inventory.AddLocation("DOCK", _site);
        _resin = Inventory.AddMaterial("PVC-K67", _kg.Id, "kg");
        _caps = Inventory.AddMaterial("CAP-1", _kg.Id, "kg");
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private InMemoryContainers Containers => _f.Stores.Containers;

    private async Task<Guid> ReceiveAsync(string number, Guid material, Guid location, decimal qty = 100m) =>
        (await new ReceiveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time, _f.Stores.ExpectedReceipts)
            .HandleAsync(new ReceiveLotCommand(material, location, number, qty, _kg.Id), CancellationToken.None)).LotId;

    private Task<Contracts.Common.CreatedResponse> CreateAsync(string? code = null, string kind = ContainerKinds.Pallet) =>
        new CreateContainerHandler(_f.Stores.UnitOfWork, Inventory, Containers, _f.Stores.Events, _f.Events, _f.Ids, new FixedRequestContext("dock"), _f.Time)
            .HandleAsync(new CreateContainerCommand(_site, code, kind, _stores.Id), CancellationToken.None);

    private Task<Contracts.Inventory.LotTransactionResponse> PackAsync(Guid lot, Guid container) =>
        new PackLotHandler(_f.Stores.UnitOfWork, Inventory, Containers, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new PackLotCommand(lot, Inventory.Find(lot)!.Version, container), CancellationToken.None);

    [Fact]
    public void A_lot_leaves_its_container_when_it_moves_alone_or_is_used_up_but_not_when_it_moves_with_it()
    {
        var container = Guid.CreateVersion7();
        var lot = Lot.Create(Guid.CreateVersion7(), _site, _resin.Id, "R-1", _kg.Id, _stores.Id, false, LotOrigin.Received).Add(10m, LotEventKind.Received).Lot;

        var (packed, packing) = lot.PackInto(container, _dock.Id);
        Assert.Equal((container, _dock.Id), (packed.ContainerId!.Value, packed.LocationId!.Value));
        Assert.Equal((LotEventKind.Packed, _stores.Id, _dock.Id), (packing.Kind, packing.FromLocationId!.Value, packing.ToLocationId!.Value));
        Assert.Throws<DomainRuleException>(() => packed.PackInto(container, _dock.Id));

        Assert.Equal(container, packed.MoveWithContainer(_stores.Id).Lot.ContainerId);
        Assert.Null(packed.MoveTo(_stores.Id).Lot.ContainerId);
        Assert.Null(packed.Take(10m, LotEventKind.Consumed, "kg").Lot.ContainerId);
        Assert.Equal(container, packed.Take(4m, LotEventKind.Consumed, "kg").Lot.ContainerId);
        Assert.Null(packed.Split(4m, Guid.CreateVersion7(), "R-1-1", _dock.Id, "kg").Part.ContainerId);
        Assert.Equal((LotEventKind.Unpacked, (Guid?)null), (packed.Unpack().Movement.Kind, packed.Unpack().Lot.ContainerId));
        Assert.Throws<DomainRuleException>(() => lot.Unpack());
    }

    [Theory]
    [InlineData("PUL1L000123", ScanCode.Lot, 123L, null)]
    [InlineData(" pul1c42\r\n", ScanCode.Container, 42L, null)]
    [InlineData("PUL1E:RM-STORE", ScanCode.Location, null, "RM-STORE")]
    [InlineData("R-24-0917", ScanCode.FreeText, null, "R-24-0917")]
    [InlineData("PUL1Lx1", ScanCode.FreeText, null, "PUL1Lx1")]
    public void A_scan_is_read_as_a_pulaski_label_or_as_free_text(string scanned, string kind, long? labelNo, string? text)
    {
        Assert.Equal(new ScanCode(kind, labelNo, text), ScanCode.Parse(scanned));
        Assert.Equal("PUL1L7", ScanCode.ForLot(7));
    }

    [Fact]
    public async Task A_container_gets_a_code_from_its_kind_and_moving_it_moves_every_lot_in_it_each_with_its_own_movement()
    {
        var pallet = await CreateAsync();
        Assert.Equal($"PAL-{Containers.LabelOf(pallet.Id)}", Containers.Get(pallet.Id).Code);
        await Assert.ThrowsAsync<ConflictException>(() => CreateAsync(Containers.Get(pallet.Id).Code.ToLowerInvariant()));

        var resin = await ReceiveAsync("R-1", _resin.Id, _stores.Id);
        var caps = await ReceiveAsync("C-1", _caps.Id, _dock.Id);
        await PackAsync(resin, pallet.Id);
        await PackAsync(caps, pallet.Id); // moved from the dock to where the pallet is
        Assert.Equal((pallet.Id, _stores.Id), (Inventory.Find(caps)!.Value.ContainerId!.Value, Inventory.Find(caps)!.Value.LocationId!.Value));

        var container = Containers.Get(pallet.Id);
        var version = (await Containers.GetForUpdateAsync(pallet.Id, CancellationToken.None))!.Version;
        await new MoveContainerHandler(_f.Stores.UnitOfWork, Inventory, Containers, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new MoveContainerCommand(container.Id, version, _dock.Id), CancellationToken.None);

        Assert.Equal(_dock.Id, Containers.Get(pallet.Id).LocationId);
        Assert.All([resin, caps], id => Assert.Equal((_dock.Id, pallet.Id), (Inventory.Find(id)!.Value.LocationId!.Value, Inventory.Find(id)!.Value.ContainerId!.Value)));
        var moves = Inventory.Events.Where(e => e.Movement.Kind == LotEventKind.Moved).ToList();
        Assert.Equal(2, moves.Count);
        Assert.All(moves, m => Assert.Equal(pallet.Id, m.ContainerId));
        Assert.Equal(EventTypes.ContainerMoved, _f.Stores.Events.Appended[^1].Type);

        var retire = new RetireContainerHandler(_f.Stores.UnitOfWork, Containers, _f.Stores.Events, _f.Events);
        var current = (await Containers.GetForUpdateAsync(pallet.Id, CancellationToken.None))!.Version;
        var full = await Assert.ThrowsAsync<DomainRuleException>(() => retire.HandleAsync(new RetireContainerCommand(pallet.Id, current), CancellationToken.None));
        Assert.Contains("still holds 2 lots", full.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scan_names_one_thing_offers_a_choice_when_text_matches_several_and_says_when_nothing_matches()
    {
        var pallet = await CreateAsync("P-7");
        await ReceiveAsync("P-7", _resin.Id, _stores.Id); // a lot numbered like the pallet
        var resolve = new ResolveScanHandler(Containers);

        var byLabel = await resolve.HandleAsync(new ResolveScanQuery(_site, ScanCode.ForContainer(Containers.LabelOf(pallet.Id))), CancellationToken.None);
        Assert.Equal(("found", pallet.Id), (byLabel.Status, Assert.Single(byLabel.Matches).Id));

        var both = await resolve.HandleAsync(new ResolveScanQuery(_site, "p-7"), CancellationToken.None);
        Assert.Equal(("ambiguous", 2), (both.Status, both.Matches.Count));

        var none = await resolve.HandleAsync(new ResolveScanQuery(_site, "PUL1C999999"), CancellationToken.None);
        Assert.Equal("not_found", none.Status);
        Assert.Contains("Pulaski label", none.Message, StringComparison.Ordinal);
    }
}
