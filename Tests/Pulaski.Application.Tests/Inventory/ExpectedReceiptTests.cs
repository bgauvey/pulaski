using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Units;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Supply on its way (D-058): expected receipts, their import from the ERP, and the shortage check that counts them.</summary>
public class ExpectedReceiptTests
{
    private static readonly DateTimeOffset Now = HandlerFixture.Now;

    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Unit _kg;
    private readonly Unit _lb;
    private readonly InventoryLocation _stores;
    private readonly InventoryMaterial _resin;

    public ExpectedReceiptTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _lb = _f.Stores.Units.AddUnit("lb", "mass", 0.45359237m);
        _stores = Inventory.AddLocation("RM-STORE", _site);
        _resin = Inventory.AddMaterial("PVC-K67", _kg.Id, "kg");
        Inventory.UomCodes["kg"] = _kg.Id;
        Inventory.AddSite(_site);
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private InMemoryExpectedReceipts Receipts => _f.Stores.ExpectedReceipts;

    private Task<Contracts.Common.CreatedResponse> CreateAsync(decimal qty, DateTimeOffset at, Guid? uom = null) =>
        new CreateExpectedReceiptHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, Receipts, _f.Stores.Events, _f.Events, _f.Ids, new FixedRequestContext("buyer"), _f.Time)
            .HandleAsync(new CreateExpectedReceiptCommand(_site, _resin.Id, qty, uom ?? _kg.Id, at, "PO-4711", "Acme", null), CancellationToken.None);

    private Task<Contracts.Inventory.ImportExpectedReceiptResponse> ImportAsync(decimal qty, bool cancelled = false) =>
        new ImportExpectedReceiptHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, Receipts, _f.Stores.Events, _f.Events, _f.Ids, new FixedRequestContext("erp"), _f.Time)
            .HandleAsync(new ImportExpectedReceiptCommand(_site, "ERP1", "PO-1/10", "PVC-K67", qty, "kg", Now.AddDays(2), "PO-1", "Acme", cancelled), CancellationToken.None);

    [Fact]
    public void Supply_due_by_a_jobs_time_covers_it_after_stock_and_supply_arriving_later_doesnt()
    {
        var resin = Guid.CreateVersion7();
        ComponentDemand Job(string number, double hours, decimal qty) => new(Guid.CreateVersion7(), number, resin, Now.AddHours(hours), qty);

        var result = Assert.Single(ShortageCalculator.Calculate(
            [Job("WO-1", 1, 100m), Job("WO-2", 10, 100m), Job("WO-3", 30, 100m)],
            new Dictionary<Guid, decimal> { [resin] = 120m },
            [new IncomingSupply(resin, Now.AddHours(5), 50m), new IncomingSupply(resin, Now.AddHours(40), 500m)]));

        // WO-1 from stock; WO-2 takes the last 20 of stock and 50 due at 5h, 30 short; WO-3 is due before the 500 arrive.
        Assert.Equal([(100m, 0m, 0m), (20m, 50m, 30m), (0m, 0m, 100m)], result.Lines.Select(l => (l.FromStock, l.FromIncoming, l.Uncovered)));
        Assert.Equal((130m, 550m), (result.Shortfall, result.Incoming));
        Assert.Equal("WO-2", result.FirstShort!.Demand.JobNumber);

        // A job with no time can use everything on its way.
        var untimed = Assert.Single(ShortageCalculator.Calculate(
            [new ComponentDemand(Guid.CreateVersion7(), "WO-9", resin, null, 300m)], new Dictionary<Guid, decimal>(), [new IncomingSupply(resin, Now.AddDays(9), 300m)]));
        Assert.Equal(0m, untimed.Shortfall);
    }

    [Fact]
    public async Task An_expected_receipt_is_changed_or_cancelled_while_open_and_closed_by_receiving_against_it()
    {
        var created = await CreateAsync(1000m, Now.AddDays(3), _lb.Id);
        Assert.Equal(("buyer", ExpectedReceipt.Open), (Receipts.Get(created.Id).CreatedBy, Receipts.Get(created.Id).Status));

        var changed = await new UpdateExpectedReceiptHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, Receipts, _f.Stores.Events, _f.Events)
            .HandleAsync(new UpdateExpectedReceiptCommand(created.Id, created.Version, 500m, _kg.Id, Now.AddDays(4), "PO-4711", "Acme", "Split delivery"), CancellationToken.None);

        var lot = await new ReceiveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time, Receipts)
            .HandleAsync(new ReceiveLotCommand(_resin.Id, _stores.Id, "R-1", 480m, _kg.Id, ExpectedReceiptId: created.Id), CancellationToken.None);
        Assert.Equal((ExpectedReceipt.Received, lot.LotId), (Receipts.Get(created.Id).Status, Receipts.Get(created.Id).ReceivedLotId!.Value));
        Assert.Equal(EventTypes.ExpectedReceiptReceived, _f.Stores.Events.Appended[^1].Type);

        await Assert.ThrowsAsync<DomainRuleException>(() => new CancelExpectedReceiptHandler(_f.Stores.UnitOfWork, Receipts, _f.Stores.Events, _f.Events)
            .HandleAsync(new CancelExpectedReceiptCommand(created.Id, changed.Version + 1), CancellationToken.None));
        await Assert.ThrowsAsync<Errors.ValidationException>(() => CreateAsync(0m, Now));
    }

    [Fact]
    public async Task The_erp_import_is_matched_on_its_key_so_sending_it_again_changes_nothing()
    {
        var first = await ImportAsync(800m);
        Assert.True(first.Created);
        Assert.False((await ImportAsync(800m)).Changed);
        Assert.Single(_f.Stores.Events.Appended);

        var changed = await ImportAsync(750m);
        Assert.Equal((false, true, 750m), (changed.Created, changed.Changed, Receipts.Get(first.Id).Quantity));
        await ImportAsync(750m, cancelled: true);
        Assert.Equal(ExpectedReceipt.Cancelled, Receipts.Get(first.Id).Status);

        // Once closed in Pulaski, the ERP's later word changes nothing.
        Assert.False((await ImportAsync(900m)).Changed);
        Assert.Equal(ExpectedReceipt.Cancelled, Receipts.Get(first.Id).Status);
    }

    [Fact]
    public async Task The_shortage_check_counts_open_receipts_beside_stock_in_the_base_unit()
    {
        await CreateAsync(2204.62262m, Now.AddHours(1), _lb.Id); // 1000 kg
        Inventory.Demand.Add(new ComponentDemandRow
        {
            JobId = Guid.CreateVersion7(),
            JobNumber = "WO-1",
            JobStatus = "dispatched",
            LineCode = "EXT-01",
            NeededFrom = Now.AddHours(2),
            PlannedStart = Now.AddHours(2),
            MaterialId = _resin.Id,
            MaterialCode = _resin.Code,
            MaterialName = "PVC",
            BaseUomId = _kg.Id,
            BaseUomCode = "kg",
            PlannedQty = 1200m,
            UomId = _kg.Id,
            UomCode = "kg",
        });
        Inventory.Availability.Add(new MaterialAvailabilityRow { MaterialId = _resin.Id, Available = 150m });

        var report = await new GetShortagesHandler(Inventory, _f.Stores.Units, _f.Time, null, Receipts)
            .HandleAsync(new GetShortagesQuery(_site, LiveTiming: false), CancellationToken.None);

        var resin = Assert.Single(report.Materials);
        Assert.Equal((150m, Now.AddHours(1)), (resin.Available, resin.NextIncomingAt!.Value));
        Assert.InRange(resin.Shortfall, 49.99m, 50.01m);
        Assert.InRange(resin.Incoming, 999.99m, 1000.01m);
        var line = Assert.Single(resin.Lines);
        Assert.Equal(("published", 150m), (line.TimeSource, line.FromStock));
        Assert.False(report.LiveTiming);
    }
}
