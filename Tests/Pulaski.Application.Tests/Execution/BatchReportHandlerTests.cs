using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Execution;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Execution;
using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Execution;

/// <summary>Reporting whole batches, and partial batches when a line stops (D-036).</summary>
public class BatchReportHandlerTests
{
    private readonly HandlerFixture _f = new();

    // Someone other than the reporter decides on a partial batch (D-037).
    private IEventFactory Decider => new EventFactory(_f.Ids, _f.Time, new FixedRequestContext("quality-user"));
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _kg;
    private readonly InventoryLocation _staging;
    private readonly InventoryReason _scrap;
    private readonly InventoryReason _breakdown;

    public BatchReportHandlerTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m).Id;
        _staging = Inventory.AddLocation("STAGE", _site);
        _scrap = Inventory.AddReason(ReasonKinds.Scrap, "OFF-SPEC");
        _breakdown = Inventory.AddReason(ReasonKinds.RunEnd, "BREAKDOWN");
        Execution.Reasons[(ReasonKinds.Hold, "PARTIAL")] = Inventory.AddReason(ReasonKinds.Hold, "PARTIAL").Id;
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private InMemoryExecution Execution => _f.Stores.Execution;

    /// <summary>A running job of a material made in batches of 50 kg, its run started two hours ago, no rate to check.</summary>
    private (InventoryJob Job, JobRun Run, InventoryMaterial Material) Running(string rule)
    {
        var material = Inventory.AddMaterial("BLEND-" + rule, _kg, "kg", batchSize: 50m, batchRule: rule);
        var job = Inventory.AddJob("J-" + rule, _site, material.Id, status: JobStatus.Running, uomId: _kg, plannedQty: 500m);
        var run = Execution.OpenRun(job.Id, job.EquipmentId, HandlerFixture.Now.AddHours(-2), _kg);
        return (job, run, material);
    }

    private ProductionReporter Reporter() => new(Inventory, _f.Stores.Units, Execution, _f.Stores.Events, _f.Events, _f.Ids);

    private Task<ReportProductionResponse> Report(InventoryJob job, IReadOnlyList<decimal> good, IReadOnlyList<decimal>? scrap = null, decimal? partial = null, Guid? run = null, bool onStop = false) =>
        Reporter().ReportAsync(
            new ReportProductionCommand(job.Id, run, 0m, 0m, null, scrap is { Count: > 0 } ? _scrap.Id : null, _staging.Id)
            {
                GoodBatches = good,
                ScrapBatches = scrap ?? [],
                PartialQty = partial,
            },
            onStop, HandlerFixture.Now, CancellationToken.None);

    [Fact]
    public async Task Whole_batches_are_reported_one_lot_each_within_the_tolerance()
    {
        var (job, run, _) = Running(BatchRule.Near);

        var report = await Report(job, [50m, 49.3m], [50.4m]);

        Assert.Equal((99.3m, 50.4m, 2), (report.RunGoodQty, report.RunScrapQty, report.Lots.Count));
        Assert.Equal([50m, 49.3m], report.Lots.Select(l => l.Quantity));
        Assert.Contains("isn't a batch", (await Assert.ThrowsAsync<DomainRuleException>(() => Report(job, [40m]))).Message, StringComparison.Ordinal);
        var quantities = await Assert.ThrowsAsync<DomainRuleException>(() => Reporter().ReportAsync(
            new ReportProductionCommand(job.Id, null, 100m, 0m, null, null, _staging.Id), false, HandlerFixture.Now, CancellationToken.None));
        Assert.Contains("report each batch", quantities.Message, StringComparison.Ordinal);
        Assert.Equal(149.7m, Execution.FindRun(run.Id)!.TotalQty);
    }

    [Fact]
    public async Task A_partial_batch_comes_only_with_a_stop_and_only_once_per_run()
    {
        var (job, run, _) = Running(BatchRule.Near);

        var notOnStop = await Assert.ThrowsAsync<DomainRuleException>(() => Report(job, [], partial: 30m));
        Assert.Contains("when the line stops", notOnStop.Message, StringComparison.Ordinal);

        var report = await Report(job, [], partial: 30m, run: run.Id, onStop: true);
        Assert.Contains(report.Warnings, w => w.Code == "partial_batch");
        var partial = Assert.Single(Execution.PartialBatches);
        Assert.Equal((PartialBatchStatus.Accepted, 30m), (partial.Status, partial.Quantity));
        Assert.Equal(LotStatus.Available, Inventory.Find(partial.LotId)!.Value.Status);
        await Assert.ThrowsAsync<DomainRuleException>(() => Report(job, [], partial: 20m, run: run.Id, onStop: true));
    }

    [Fact]
    public async Task A_finished_goods_partial_waits_on_hold_and_is_approved_or_rejected()
    {
        var (job, run, _) = Running(BatchRule.Exact);
        await Assert.ThrowsAsync<DomainRuleException>(() => Report(job, [49.3m]));
        await Report(job, [50m]);

        var report = await Report(job, [], partial: 30m, run: run.Id, onStop: true);
        Assert.Contains(report.Warnings, w => w.Code == "partial_batch_pending");
        var partial = Assert.Single(Execution.PartialBatches);
        Assert.Equal((PartialBatchStatus.Pending, LotStatus.OnHold), (partial.Status, Inventory.Find(partial.LotId)!.Value.Status));

        var reject = new RejectPartialBatchHandler(_f.Stores.UnitOfWork, Inventory, Execution, _f.Stores.Units, _f.Stores.Events, Decider, _f.Ids, _f.Time);
        await reject.HandleAsync(new RejectPartialBatchCommand(partial.Id, _scrap.Id, "Short fill"), CancellationToken.None);

        Assert.Equal(LotStatus.Scrapped, Inventory.Find(partial.LotId)!.Value.Status);
        Assert.Equal((50m, 30m), (Execution.FindRun(run.Id)!.GoodQty, Execution.FindRun(run.Id)!.ScrapQty));
        Assert.Contains(Execution.Counts, c => c.CountKind == CountKind.Defective && c.DeltaQty == 30m && c.ScrapReasonCodeId == _scrap.Id);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            new ApprovePartialBatchHandler(_f.Stores.UnitOfWork, Inventory, Execution, _f.Stores.Events, Decider, _f.Ids, _f.Time)
                .HandleAsync(new ApprovePartialBatchCommand(partial.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Whoever_reported_a_partial_cant_decide_on_it()
    {
        var (job, run, _) = Running(BatchRule.Exact);
        await Report(job, [], partial: 30m, run: run.Id, onStop: true);
        var partial = Assert.Single(Execution.PartialBatches);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new ApprovePartialBatchHandler(_f.Stores.UnitOfWork, Inventory, Execution, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
                .HandleAsync(new ApprovePartialBatchCommand(partial.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new RejectPartialBatchHandler(_f.Stores.UnitOfWork, Inventory, Execution, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
                .HandleAsync(new RejectPartialBatchCommand(partial.Id, _scrap.Id, null), CancellationToken.None));
        Assert.Equal(PartialBatchStatus.Pending, Execution.PartialBatches[0].Status);
    }

    [Fact]
    public async Task Approving_releases_the_lot_and_a_late_partial_needs_a_run_that_stopped_with_a_reason()
    {
        var (job, run, _) = Running(BatchRule.Exact);
        await Execution.UpdateRunAsync(run.End(HandlerFixture.Now, null), CancellationToken.None);
        var noReason = await Assert.ThrowsAsync<DomainRuleException>(() => Report(job, [], partial: 30m, run: run.Id));
        Assert.Contains("ended without a reason", noReason.Message, StringComparison.Ordinal);

        await Execution.UpdateRunAsync(run.End(HandlerFixture.Now, _breakdown.Id), CancellationToken.None);
        await Report(job, [], partial: 30m, run: run.Id);
        var partial = Assert.Single(Execution.PartialBatches);

        await new ApprovePartialBatchHandler(_f.Stores.UnitOfWork, Inventory, Execution, _f.Stores.Events, Decider, _f.Ids, _f.Time)
            .HandleAsync(new ApprovePartialBatchCommand(partial.Id, "Customer accepts"), CancellationToken.None);

        Assert.Equal(PartialBatchStatus.Approved, Execution.PartialBatches[0].Status);
        Assert.Equal(LotStatus.Available, Inventory.Find(partial.LotId)!.Value.Status);
        Assert.Equal(EventTypes.LotStatusChanged, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task A_material_made_continuously_takes_no_batches()
    {
        var material = Inventory.AddMaterial("CONT", _kg, "kg");
        var job = Inventory.AddJob("J-C", _site, material.Id, status: JobStatus.Running, uomId: _kg, plannedQty: 100m);
        Execution.OpenRun(job.Id, job.EquipmentId, HandlerFixture.Now.AddHours(-1), _kg);

        await Assert.ThrowsAsync<DomainRuleException>(() => Report(job, [50m]));
    }
}
