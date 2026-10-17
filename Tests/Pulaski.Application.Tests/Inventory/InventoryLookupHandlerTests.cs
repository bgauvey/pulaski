using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>The shortage check and the movement journal (D-029).</summary>
public class InventoryLookupHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _resin = Guid.CreateVersion7();

    private GetShortagesHandler Shortages() => new(_f.Stores.Inventory, _f.Stores.Units, _f.Time);

    private ComponentDemandRow Row(Guid job, string number, DateTimeOffset? at, decimal planned, Pulaski.Domain.Units.Unit uom, Pulaski.Domain.Units.Unit baseUom, decimal consumed = 0m) => new()
    {
        JobId = job,
        JobNumber = number,
        JobStatus = "dispatched",
        LineCode = "EXT-01",
        NeededFrom = at,
        MaterialId = _resin,
        MaterialCode = "PVC-K67",
        MaterialName = "PVC compound",
        BaseUomId = baseUom.Id,
        BaseUomCode = baseUom.Code,
        PlannedQty = planned,
        UomId = uom.Id,
        UomCode = uom.Code,
        ConsumedQty = consumed,
    };

    [Fact]
    public async Task Planned_units_are_converted_consumption_is_taken_off_once_and_short_materials_come_first()
    {
        var kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        var lb = _f.Stores.Units.AddUnit("lb", "mass", 0.45359237m);
        var box = _f.Stores.Units.AddUnit("box", "other", null);
        var job = Guid.CreateVersion7();
        var later = Guid.CreateVersion7();
        var inv = _f.Stores.Inventory;

        // WO-1 plans 500 kg and 2500 lb (1133.980925 kg), and has used 300 kg; WO-2 plans 400 kg, and 3 boxes that don't convert.
        inv.Demand.AddRange([
            Row(job, "WO-1", HandlerFixture.Now.AddHours(2), 500m, kg, kg, consumed: 300m),
            Row(job, "WO-1", HandlerFixture.Now.AddHours(2), 2500m, lb, kg, consumed: 300m),
            Row(later, "WO-2", HandlerFixture.Now.AddDays(2), 400m, kg, kg),
            Row(later, "WO-2", HandlerFixture.Now.AddDays(2), 3m, box, kg),
        ]);
        inv.Availability.Add(new MaterialAvailabilityRow { MaterialId = _resin, Available = 1500m, Held = 200m });

        var report = await Shortages().HandleAsync(new GetShortagesQuery(_site), CancellationToken.None);

        var resin = Assert.Single(report.Materials);
        Assert.Equal((1500m, 200m, 1733.980925m, 233.980925m), (resin.Available, resin.Held, resin.Demand, resin.Shortfall));
        Assert.Equal(("WO-2", HandlerFixture.Now.AddDays(2)), (resin.FirstShortJobNumber, resin.FirstShortAt));
        Assert.Equal([1333.980925m, 400m], resin.Lines.Select(l => l.Remaining));
        Assert.Equal("uom_mismatch", Assert.Single(report.Problems).Code);

        var soon = await Shortages().HandleAsync(new GetShortagesQuery(_site, HorizonDays: 1), CancellationToken.None);
        Assert.Equal(0m, Assert.Single(soon.Materials).Shortfall);
        await Assert.ThrowsAsync<ValidationException>(() => Shortages().HandleAsync(new GetShortagesQuery(_site, HorizonDays: 0), CancellationToken.None));
    }

    [Fact]
    public async Task The_journal_defaults_to_the_last_seven_days_and_checks_its_range_and_cursor()
    {
        var journal = new ListMovementsHandler(_f.Stores.Inventory, _f.Time);

        await journal.HandleAsync(new ListMovementsQuery(_site, Kind: " consumed ", Actor: " "), CancellationToken.None);
        var filter = _f.Stores.Inventory.LastJournalFilter!;
        Assert.Equal((HandlerFixture.Now.AddDays(-7), HandlerFixture.Now, "consumed", (string?)null), (filter.From, filter.To, filter.Kind, filter.Actor));

        await Assert.ThrowsAsync<ValidationException>(() => journal.HandleAsync(
            new ListMovementsQuery(_site, From: HandlerFixture.Now, To: HandlerFixture.Now.AddDays(-1)), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => journal.HandleAsync(
            new ListMovementsQuery(_site, From: HandlerFixture.Now.AddDays(-400)), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => journal.HandleAsync(
            new ListMovementsQuery(_site, BeforeAt: HandlerFixture.Now), CancellationToken.None));
    }
}
