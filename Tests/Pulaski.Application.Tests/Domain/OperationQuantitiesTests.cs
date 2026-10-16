using Pulaski.Domain.Boms;
using Pulaski.Domain.Processes;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Quantities per operation (D-024) and components for one job (D-025), on Extrude (EXT-01, EXT-02) then Pack (PACK1).</summary>
public class OperationQuantitiesTests
{
    private static readonly Guid Ext1 = Guid.CreateVersion7();
    private static readonly Guid Ext2 = Guid.CreateVersion7();
    private static readonly Guid Pack1 = Guid.CreateVersion7();
    private static readonly Guid Elsewhere = Guid.CreateVersion7();

    private static readonly ProcessOperation Extrude =
        new(Guid.CreateVersion7(), 10, "Extrude", null, 0, 0, [new OperationEquipment(Ext1, 50m), new OperationEquipment(Ext2, 50m)]);

    private static readonly ProcessOperation Pack =
        new(Guid.CreateVersion7(), 30, "Pack", null, 0, 0, [new OperationEquipment(Pack1, 100m)]);

    // Listed pack-first, so flow order has to come from the link, not the list.
    private static readonly ProcessDefinition ExtrudeAndPack = new(
        [Pack, Extrude],
        [new ProcessDependency(Guid.CreateVersion7(), Extrude.Id, Pack.Id, DependencyType.AfterStart, TimeSpan.Zero)]);

    [Fact]
    public void An_order_is_placed_as_far_as_its_least_placed_operation()
    {
        (Guid?, decimal)[] jobs = [(Extrude.Id, 1200m), (Extrude.Id, 1200m), (Pack.Id, 2400m)];

        Assert.Equal(2400m, OperationQuantities.Placed(ExtrudeAndPack, jobs));
        Assert.Equal(1200m, OperationQuantities.Placed(ExtrudeAndPack, jobs[1..]));
        Assert.Equal(0m, OperationQuantities.Placed(ExtrudeAndPack, [(Extrude.Id, 2400m)]));
        Assert.Equal(
            [(10, 2400m), (30, 2400m)],
            OperationQuantities.ByOperation(ExtrudeAndPack, jobs).Select(o => (o.Operation.Number, o.Quantity)));
    }

    [Fact]
    public void Without_a_process_the_jobs_add_up()
    {
        Assert.Equal(3000m, OperationQuantities.Placed(null, [(null, 1000m), (null, 2000m)]));
    }

    [Fact]
    public void A_job_added_by_hand_belongs_to_the_operation_that_runs_on_its_line()
    {
        Assert.Same(Pack, ExtrudeAndPack.OperationFor(Pack1));
        Assert.Same(Extrude, ExtrudeAndPack.OperationFor(Ext2));
        Assert.Same(Extrude, ExtrudeAndPack.OperationFor(Elsewhere));
        Assert.Null(ProcessDefinition.Empty.OperationFor(Ext1));
    }

    [Fact]
    public void One_job_takes_the_items_of_its_operation_for_its_quantity()
    {
        var kg = Guid.CreateVersion7();
        var bom = new BomDefinition(1000m, Guid.CreateVersion7(),
        [
            new(Guid.CreateVersion7(), 10, Guid.CreateVersion7(), 1450m, kg, 0.02m, ConsumptionMethod.TimeWindow, 10),
            new(Guid.CreateVersion7(), 20, Guid.CreateVersion7(), 90m, kg, 0m, ConsumptionMethod.TimeWindow, null),
            new(Guid.CreateVersion7(), 30, Guid.CreateVersion7(), 84m, kg, 0m, ConsumptionMethod.Backflush, 30),
        ]);

        // An item naming no operation goes to the first one, Extrude.
        Assert.Equal([(10, 739.5m), (20, 45m)], BomExplosion.ForJob(bom, 500m, 10, 10).Select(c => (c.Item.LineNo, c.PlannedQty)));
        Assert.Equal([(30, 42m)], BomExplosion.ForJob(bom, 500m, 30, 10).Select(c => (c.Item.LineNo, c.PlannedQty)));
        Assert.Equal([30], bom.MissingOperations([10, 20]));
        Assert.Empty(bom.MissingOperations([10, 30]));
    }
}
