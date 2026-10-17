using Pulaski.Application.Materials;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Scheduling;

/// <summary>Jobs of a material made in batches are whole batches (D-036).</summary>
public class BatchPlanningHandlerTests : SchedulingHandlerTestBase
{
    public BatchPlanningHandlerTests() => F.Stores.Boms.AddMaterial(White, Metre, batchSize: 50m);

    [Fact]
    public async Task A_job_is_whole_batches()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(line, wo, "J-1", qty: 125m));
        Assert.Contains("Plan 100 or 150", ex.Message, StringComparison.Ordinal);
        await CreateProductionAsync(line, wo, "J-2", qty: 150m);
    }

    [Fact]
    public async Task A_replacement_rounds_what_is_left_up_to_whole_batches()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White, WorkOrderStatus.InProgress);
        var job = F.Stores.Scheduling.AddJob(global::Pulaski.Domain.Scheduling.Job.CreateProduction(
            Guid.CreateVersion7(), Site, wo.Id, "J-9", line.Id, White, 200m, Metre, 1m, null) with
        { Status = JobStatus.Running });
        F.Stores.Scheduling.SetGoodQty(job.Id, 125m);

        var cancel = new CancelJobHandler(
            F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time);
        var result = await cancel.HandleAsync(new CancelJobCommand(job.Id, Version(job.Id), "Line down", Replace: true), CancellationToken.None);

        Assert.Equal(100m, F.Stores.Scheduling.FindJob(result.ReplacementJobId!.Value)!.Value.PlannedQty);
    }
}

/// <summary>Setting a material's batching (D-036).</summary>
public class SetBatchingHandlerTests
{
    private readonly HandlerFixture _f = new();

    [Fact]
    public async Task The_rule_defaults_from_the_type_and_open_orders_that_no_longer_fit_are_named()
    {
        var kg = _f.Stores.Units.AddUnit("kg", "mass", 1m).Id;
        var admin = _f.Stores.MaterialAdmin;
        admin.AddUom("kg", id: kg);
        var material = Material.CreateLocal(Guid.CreateVersion7(), new MaterialChange("FG-1", "Finished", null, "finished", null, kg, true, null, true));
        var version = await admin.InsertAsync(material, HandlerFixture.Now, CancellationToken.None);
        admin.OpenOrders[material.Id] = [("WO-1", 100m, kg), ("WO-2", 125m, kg)];

        var handler = new SetBatchingHandler(_f.Stores.UnitOfWork, admin, _f.Stores.Units, _f.Stores.Events, _f.Events);
        var result = await handler.HandleAsync(new SetBatchingCommand(material.Id, version, 50m, 2m, null), CancellationToken.None);

        Assert.Equal((50m, BatchRule.Exact), (admin.Batching[material.Id].Size, admin.Batching[material.Id].Rule));
        Assert.Contains("WO-2", Assert.Single(result.StillUsedBy), StringComparison.Ordinal);
        Assert.DoesNotContain("WO-1", result.StillUsedBy[0], StringComparison.Ordinal);

        await handler.HandleAsync(new SetBatchingCommand(material.Id, result.Version, null, 2m, null), CancellationToken.None);
        Assert.Null(admin.Batching[material.Id].Size);
    }
}
