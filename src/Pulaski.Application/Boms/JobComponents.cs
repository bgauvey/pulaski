using Pulaski.Application.Abstractions;
using Pulaski.Application.Processes;
using Pulaski.Application.Units;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Processes;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Boms;

/// <summary>
/// Gives a production job added to an order after it was made (from the schedule, or as the
/// replacement for a cancelled job) its operation and its planned components, as creating the
/// order does for the jobs it makes (D-024, D-025).
/// </summary>
internal sealed class JobComponents(IProcessRepository processes, IBomRepository boms, IUnitRepository units, IIdGenerator ids)
{
    /// <summary>
    /// The job with its operation: the one it already has, or else the operation of the order's
    /// process that runs on its line or group (the first operation when none does).
    /// </summary>
    public async Task<Job> WithOperationAsync(Job job, WorkOrder order, CancellationToken cancellationToken)
    {
        if (job.ProcessOperationId is not null || await DefinitionAsync(order, cancellationToken) is not { } definition)
        {
            return job;
        }

        return definition.OperationFor(job.GroupEquipmentId ?? job.EquipmentId) is { } operation
            ? job with { ProcessOperationId = operation.Id }
            : job;
    }

    /// <summary>Writes the job's planned components from the order's BOM version; nothing when the order has none.</summary>
    public async Task PlanAsync(Job job, WorkOrder order, CancellationToken cancellationToken)
    {
        if (order.BomVersionId is not { } bomVersionId
            || job.PlannedQty is not { } quantity
            || await boms.GetVersionAsync(bomVersionId, cancellationToken) is not { } bom
            || await DefinitionAsync(order, cancellationToken) is not { } definition
            || definition.InFlowOrder() is not [var first, ..])
        {
            return;
        }

        var operation = definition.Operations.FirstOrDefault(o => o.Id == job.ProcessOperationId) ?? first;
        var converter = UnitConversions.For(
            order.MaterialId, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([order.MaterialId], cancellationToken));
        var inBomUnit = converter.Convert(quantity, job.UomId ?? order.UomId, bom.Definition.BaseUomId);

        var rows = BomExplosion.ForJob(bom.Definition, inBomUnit, operation.Number, first.Number)
            .Select(c => new JobMaterial(
                ids.NewId(), job.Id, c.Item.ComponentMaterialId, JobMaterial.Consume, c.PlannedQty, c.Item.UomId, c.Item.ConsumptionMethod, c.Item.Id))
            .ToList();
        await boms.InsertJobMaterialsAsync(rows, cancellationToken);
    }

    private async Task<ProcessDefinition?> DefinitionAsync(WorkOrder order, CancellationToken cancellationToken) =>
        order.ProcessVersionId is { } versionId
            ? (await processes.GetVersionAsync(versionId, cancellationToken))?.Definition
            : null;
}
