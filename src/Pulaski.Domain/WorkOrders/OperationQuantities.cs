using Pulaski.Domain.Processes;

namespace Pulaski.Domain.WorkOrders;

/// <summary>The quantity an order's open jobs plan for one of its operations.</summary>
public sealed record OperationQuantity(ProcessOperation Operation, decimal Quantity);

/// <summary>
/// How much of a work order is on lines (D-024). Every operation of its process makes the whole
/// quantity (extrude 2,400 m, then pack the same 2,400 m), so the jobs of one operation are
/// added up, never the jobs of all of them: the order is placed as far as its least-placed
/// operation is.
/// </summary>
public static class OperationQuantities
{
    /// <summary>Each operation of the process with what its open jobs plan, in flow order.</summary>
    public static IReadOnlyList<OperationQuantity> ByOperation(ProcessDefinition definition, IEnumerable<(Guid? OperationId, decimal Quantity)> jobs)
    {
        var totals = jobs.Where(j => j.OperationId is not null)
            .GroupBy(j => j.OperationId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(j => j.Quantity));
        return [.. definition.InFlowOrder().Select(o => new OperationQuantity(o, totals.GetValueOrDefault(o.Id)))];
    }

    /// <summary>
    /// The order quantity on lines: the least any operation plans, or the plain sum of the jobs for
    /// an order with no process (<paramref name="definition"/> null or empty).
    /// </summary>
    public static decimal Placed(ProcessDefinition? definition, IEnumerable<(Guid? OperationId, decimal Quantity)> jobs)
    {
        var list = jobs.ToList();
        return definition is null || definition.Operations.Count == 0
            ? list.Sum(j => j.Quantity)
            : ByOperation(definition, list).Min(o => o.Quantity);
    }
}
