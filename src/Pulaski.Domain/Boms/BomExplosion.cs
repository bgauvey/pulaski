using Pulaski.Domain.Processes;

namespace Pulaski.Domain.Boms;

/// <summary>A component one job of a new work order needs: <see cref="JobIndex"/> is the job's place in the process plan.</summary>
public sealed record PlannedComponent(int JobIndex, BomItem Item, decimal PlannedQty);

/// <summary>
/// Plans a new work order's components from its BOM, one level deep (D-025). Pure; the caller
/// writes the rows.
/// </summary>
public static class BomExplosion
{
    /// <summary>
    /// Each item's total is <paramref name="orderQty"/> ÷ base quantity × quantity per × (1 +
    /// scrap factor), rounded to six places. It goes to the jobs of the operation that consumes it
    /// (the first operation in flow order when the item names none), shared in proportion to their
    /// quantities with the last taking the remainder. Jobs that would get nothing are left out.
    /// </summary>
    /// <param name="bom">The released BOM version's definition.</param>
    /// <param name="orderQty">The work order quantity, converted to the BOM's base unit (D-026).</param>
    /// <param name="jobs">The order's jobs in flow order, as <see cref="ProcessJobPlan.For"/> returns them.</param>
    /// <exception cref="DomainRuleException">An item names an operation the process doesn't have.</exception>
    public static IReadOnlyList<PlannedComponent> For(BomDefinition bom, decimal orderQty, IReadOnlyList<OperationJob> jobs)
    {
        if (jobs.Count == 0)
        {
            return [];
        }

        var first = jobs[0].Operation.Number;
        var components = new List<PlannedComponent>();
        foreach (var item in bom.Items.OrderBy(i => i.LineNo))
        {
            var operation = item.OperationNumber ?? first;
            var consumers = jobs.Select((job, index) => (Job: job, Index: index)).Where(j => j.Job.Operation.Number == operation).ToList();
            if (consumers.Count == 0)
            {
                throw new DomainRuleException($"BOM line {item.LineNo} is consumed by operation {operation}, which the process doesn't have.");
            }

            var total = Math.Round(orderQty / bom.BaseQty * item.QtyPer * (1 + item.ScrapFactor), ProcessJobPlan.QuantityScale, MidpointRounding.AwayFromZero);
            var operationQty = consumers.Sum(c => c.Job.PlannedQty);
            var remaining = total;
            for (var i = 0; i < consumers.Count; i++)
            {
                var qty = i == consumers.Count - 1
                    ? remaining
                    : Math.Round(total * consumers[i].Job.PlannedQty / operationQty, ProcessJobPlan.QuantityScale, MidpointRounding.ToZero);
                remaining -= qty;
                if (qty > 0)
                {
                    components.Add(new PlannedComponent(consumers[i].Index, item, qty));
                }
            }
        }

        return components;
    }
}
