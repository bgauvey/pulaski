namespace Pulaski.Domain.Processes;

/// <summary>One job a work order needs from its process: an operation on one line or department, for its share of the quantity.</summary>
public sealed record OperationJob(
    ProcessOperation Operation,
    Guid EquipmentId,
    string JobNumber,
    decimal PlannedQty,
    DateTimeOffset EarliestStart);

/// <summary>
/// Turns a released process into the jobs of a new work order (D-024): one job per operation and
/// line or department, each for its share of the order quantity. Pure; the handler checks the
/// equipment and places the jobs.
/// </summary>
public static class ProcessJobPlan
{
    /// <summary>Decimal places of <c>core.qty</c>.</summary>
    public const int QuantityScale = 6;

    /// <summary>
    /// The jobs in flow order. Shares are rounded down to <see cref="QuantityScale"/> places and
    /// the last entity of an operation takes the remainder, so every operation adds up to
    /// <paramref name="quantity"/> exactly. Each job is held until <paramref name="start"/>, or
    /// later where a link's lag says so: an operation starts no earlier than the operations it
    /// follows plus the lag. That is a lower bound; the projection does not yet hold a job for
    /// the work before it.
    /// </summary>
    /// <exception cref="DomainRuleException">The definition is not releasable, or a share rounds to nothing.</exception>
    public static IReadOnlyList<OperationJob> For(ProcessDefinition definition, string orderNumber, decimal quantity, DateTimeOffset start)
    {
        if (quantity <= 0)
        {
            throw new DomainRuleException("Planned quantity must be greater than zero.");
        }

        if (definition.ReleaseProblems() is { Count: > 0 } problems)
        {
            throw new DomainRuleException(string.Join(" ", problems.Select(p => p.Message)));
        }

        var earliest = new Dictionary<Guid, DateTimeOffset>();
        var jobs = new List<OperationJob>();
        foreach (var op in definition.InFlowOrder())
        {
            var at = definition.Dependencies
                .Where(d => d.ToOperationId == op.Id)
                .Select(d => earliest[d.FromOperationId] + d.Lag)
                .Aggregate(start, (a, b) => b > a ? b : a);
            earliest[op.Id] = at;

            var remaining = quantity;
            for (var i = 0; i < op.Equipment.Count; i++)
            {
                var last = i == op.Equipment.Count - 1;
                var qty = last
                    ? remaining
                    : Math.Round(quantity * op.Equipment[i].SharePercent / 100m, QuantityScale, MidpointRounding.ToZero);
                if (qty <= 0)
                {
                    throw new DomainRuleException($"Operation {op.Number}: {quantity} is too small to share between its equipment.");
                }

                remaining -= qty;
                var number = op.Equipment.Count == 1 ? $"{orderNumber}-{op.Number}" : $"{orderNumber}-{op.Number}-{i + 1}";
                jobs.Add(new OperationJob(op, op.Equipment[i].EquipmentId, number, qty, at));
            }
        }

        return jobs;
    }
}
