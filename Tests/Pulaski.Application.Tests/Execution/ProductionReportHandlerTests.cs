using Pulaski.Application.Abstractions;
using Pulaski.Application.Execution;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Execution;
using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Units;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Execution;

/// <summary>Reporting production into runs, the rate cap, late reports and consumption checks (D-035).</summary>
public class ProductionReportHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Unit _ea;
    private readonly Unit _box;
    private readonly Unit _kg;
    private readonly InventoryLocation _staging;
    private readonly InventoryMaterial _cap;
    private readonly InventoryMaterial _resin;
    private readonly InventoryReason _startup;
    private readonly InventoryJob _job;
    private readonly JobRun _run;

    public ProductionReportHandlerTests()
    {
        _ea = _f.Stores.Units.AddUnit("ea", "count", 1m);
        _box = _f.Stores.Units.AddUnit("box", "count", 100m);
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _staging = Inventory.AddLocation("FG-STAGE", _site);
        _cap = Inventory.AddMaterial("CAP-1", _ea.Id, "ea");
        _resin = Inventory.AddMaterial("PP-1", _kg.Id, "kg");
        _startup = Inventory.AddReason(ReasonKinds.Scrap, "STARTUP");
        _job = Inventory.AddJob("J-1", _site, _cap.Id, status: "running", uomId: _ea.Id, plannedQty: 1000m);
        Inventory.PlanComponent(_job.Id, _resin.Id);
        Execution.PlannedComponents[(_job.Id, _resin.Id)] = (50m, _kg.Id);
        Execution.IdealRates[(_job.EquipmentId, _cap.Id)] = (100m, _ea.Id);

        // Running for an hour: at 100 ea/h with the default 110%, the cap is 110 ea.
        _run = Execution.OpenRun(_job.Id, _job.EquipmentId, HandlerFixture.Now.AddHours(-1), _ea.Id);
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private InMemoryExecution Execution => _f.Stores.Execution;

    private Task<ReportProductionResponse> Report(decimal good, decimal scrap = 0m, Guid? run = null, Guid? unit = null, Guid? reason = null) =>
        new ReportProductionHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, Execution, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ReportProductionCommand(_job.Id, run, good, scrap, unit, reason, good > 0 ? _staging.Id : null), CancellationToken.None);

    private Task<Contracts.Inventory.LotTransactionResponse> Consume(Guid lotId, decimal kg, Guid? run = null) =>
        new ConsumeLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, Execution, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ConsumeLotCommand(lotId, _job.Id, kg, _kg.Id, RunId: run), CancellationToken.None);

    [Fact]
    public async Task A_report_adds_to_the_run_counts_good_and_scrap_and_puts_good_output_in_a_lot()
    {
        var report = await Report(80m, 5m, reason: _startup.Id);

        Assert.Equal((_run.Id, 80m, 5m, 110m), (report.RunId, report.RunGoodQty, report.RunScrapQty, report.CapQty));
        Assert.Equal(80m, report.Lot!.Quantity);
        Assert.Equal(
            [(CountKind.Processed, 85m, (Guid?)null), (CountKind.Defective, 5m, _startup.Id)],
            Execution.Counts.Select(c => (c.CountKind, c.DeltaQty, c.ScrapReasonCodeId)));
        Assert.All(Execution.Counts, c => Assert.Equal((HandlerFixture.Now, _run.Id), (c.OccurredAt, c.RunId)));
        Assert.Equal([EventTypes.ProductionReported, EventTypes.LotProduced], _f.Stores.Events.Appended.Select(e => e.Type));
    }

    [Fact]
    public async Task A_run_cant_report_more_than_its_time_at_the_ideal_rate_allows()
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Report(1000m));
        Assert.Contains("1,000 ea in 1 h 00 min is more than EXT-01 can make", ex.Message, StringComparison.Ordinal);

        // Entered in boxes of 100: 2 boxes is 200 ea, also too many.
        await Assert.ThrowsAsync<DomainRuleException>(() => Report(2m, unit: _box.Id));
        await Report(100m);
        var over = await Assert.ThrowsAsync<DomainRuleException>(() => Report(20m));
        Assert.Contains("At most 10 ea more", over.Message, StringComparison.Ordinal);

        _f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(120m, (await Report(20m)).RunGoodQty);
    }

    [Fact]
    public async Task Production_is_reported_into_a_past_run_until_the_job_is_closed()
    {
        await Execution.UpdateRunAsync(_run.End(HandlerFixture.Now, null), CancellationToken.None);
        _f.Time.Advance(TimeSpan.FromHours(3));

        var notRunning = await Assert.ThrowsAsync<DomainRuleException>(() => Report(10m));
        Assert.Contains("choose the run it belongs to", notRunning.Message, StringComparison.Ordinal);

        var late = await Report(90m, run: _run.Id);
        Assert.Equal(90m, late.RunGoodQty);
        Assert.All(Execution.Counts, c => Assert.Equal(HandlerFixture.Now, c.OccurredAt));
        await Assert.ThrowsAsync<DomainRuleException>(() => Report(30m, run: _run.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => Report(1m, run: Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Scrap_needs_a_reason_and_good_output_a_location()
    {
        await Assert.ThrowsAsync<Errors.ValidationException>(() => Report(0m, 5m));
        await Assert.ThrowsAsync<Errors.ValidationException>(() =>
            new ReportProductionHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, Execution, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
                .HandleAsync(new ReportProductionCommand(_job.Id, null, 5m, 0m, null, null, null), CancellationToken.None));

        var scrapOnly = await Report(0m, 5m, reason: _startup.Id);
        Assert.Null(scrapOnly.Lot);
    }

    [Fact]
    public async Task No_ideal_rate_means_the_report_is_recorded_unchecked_with_a_warning()
    {
        Execution.IdealRates.Clear();

        var report = await Report(5000m);

        Assert.Equal("not_checked", Assert.Single(report.Warnings).Code);
        Assert.Null(report.CapQty);
    }

    [Fact]
    public async Task Consumption_needs_a_run_and_warns_beyond_the_planned_components()
    {
        var lot = await new ReceiveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time, _f.Stores.ExpectedReceipts)
            .HandleAsync(new ReceiveLotCommand(_resin.Id, _staging.Id, "R-1", 500m, _kg.Id), CancellationToken.None);

        Assert.Empty((await Consume(lot.LotId, 40m)).Warnings);
        Execution.Consumed[(_job.Id, _resin.Id)] = 40m;
        Assert.Equal("over_planned", Assert.Single((await Consume(lot.LotId, 30m)).Warnings).Code);

        await Execution.UpdateRunAsync(_run.End(HandlerFixture.Now, null), CancellationToken.None);
        var notRunning = await Assert.ThrowsAsync<DomainRuleException>(() => Consume(lot.LotId, 1m));
        Assert.Contains("consumption can't be recorded now", notRunning.Message, StringComparison.Ordinal);
        Assert.NotNull(await Consume(lot.LotId, 1m, _run.Id));
    }
}
