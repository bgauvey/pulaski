using Pulaski.Application.Scheduling;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Processes;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Scheduling;

/// <summary>
/// Jobs added to an order after it was made take their operation and components (D-025), and an
/// order's quantity is counted per operation (D-024). Extrude runs on two lines, Pack on one.
/// </summary>
public class JobComponentsHandlerTests : SchedulingHandlerTestBase
{
    private readonly Guid _blend = Guid.CreateVersion7();
    private readonly Guid _endCap = Guid.CreateVersion7();

    private CancelJobHandler Cancel() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time);

    private DispatchWorkOrderHandler Dispatch() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Events, F.Events, F.Time);

    /// <summary>A planned order from Extrude and Pack with a BOM: blend at Extrude, end caps at Pack, per 1000 m.</summary>
    private async Task<(WorkOrder Order, SchedulingLine Ext1, SchedulingLine Ext2, SchedulingLine Pack, ProcessDefinition Route)> OrderAsync()
    {
        var (ext1, ext2, pack) = (Line(), Line(), Line());
        var extrude = new ProcessOperation(Guid.CreateVersion7(), 10, "Extrude", null, 0, 0,
            [new OperationEquipment(ext1.Id, 50m), new OperationEquipment(ext2.Id, 50m)]);
        var packing = new ProcessOperation(Guid.CreateVersion7(), 30, "Pack", null, 0, 0, [new OperationEquipment(pack.Id, 100m)]);
        var route = new ProcessDefinition(
            [extrude, packing], [new ProcessDependency(Guid.CreateVersion7(), extrude.Id, packing.Id, DependencyType.AfterStart, TimeSpan.Zero)]);
        var process = F.Stores.Processes.AddReleased(Site, route);
        var bom = F.Stores.Boms.AddReleased(White, process.Process.Id, new BomDefinition(1000m, Metre,
        [
            new(Guid.CreateVersion7(), 10, _blend, 1450m, Guid.CreateVersion7(), 0m, ConsumptionMethod.TimeWindow, 10),
            new(Guid.CreateVersion7(), 20, _endCap, 84m, Guid.CreateVersion7(), 0m, ConsumptionMethod.Backflush, 30),
        ]));

        var wo = await WorkOrderAsync(White, qty: 2000m);
        wo = wo with { ProcessVersionId = process.Version.Id, BomVersionId = bom.Id };
        await F.Stores.WorkOrders.InsertAsync(wo, Now, CancellationToken.None);
        return (wo, ext1, ext2, pack, route);
    }

    [Fact]
    public async Task A_job_added_from_the_schedule_takes_the_operation_of_its_line_and_its_components()
    {
        var (wo, _, _, pack, route) = await OrderAsync();

        var id = await CreateProductionAsync(pack, wo, "WO-PACK", qty: 500m);

        Assert.Equal(route.Operations[1].Id, Job(id).ProcessOperationId);
        var component = Assert.Single(F.Stores.Boms.JobMaterials, m => m.JobId == id);
        Assert.Equal((_endCap, 42m), (component.MaterialId, component.PlannedQty));
    }

    [Fact]
    public async Task A_replacement_keeps_the_operation_and_plans_components_for_what_is_left()
    {
        var (wo, ext1, _, _, route) = await OrderAsync();
        var id = await CreateProductionAsync(ext1, wo, "WO-EXT", qty: 1000m);
        F.Stores.Scheduling.SetGoodQty(id, 400m);

        var result = await Cancel().HandleAsync(new CancelJobCommand(id, Version(id), "Die damage", Replace: true), CancellationToken.None);

        var replacement = Job(result.ReplacementJobId!.Value);
        Assert.Equal((route.Operations[0].Id, 600m), (replacement.ProcessOperationId, replacement.PlannedQty));
        Assert.Equal(870m, Assert.Single(F.Stores.Boms.JobMaterials, m => m.JobId == replacement.Id).PlannedQty);
    }

    [Fact]
    public async Task Dispatch_checks_each_operation_against_the_order_quantity()
    {
        var (wo, ext1, ext2, pack, _) = await OrderAsync();
        await CreateProductionAsync(ext1, wo, "WO-1", qty: 1000m);
        await CreateProductionAsync(ext2, wo, "WO-2", qty: 1000m);
        await CreateProductionAsync(pack, wo, "WO-3", qty: 1500m);

        var result = await Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);

        // Extrude makes 2000 across its two lines, so only Pack is short; the sum of all jobs (3500) is never compared.
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("quantity_mismatch", warning.Code);
        Assert.StartsWith("Operation 30 (Pack) plans 1500", warning.Message, StringComparison.Ordinal);
    }
}
