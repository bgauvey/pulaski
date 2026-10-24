namespace Pulaski.Domain.Inventory;

/// <summary>
/// What one job still needs of one component, in the component's base unit (D-029).
/// <see cref="NeededFrom"/> orders the jobs; null means the job has no time yet and comes last.
/// </summary>
public sealed record ComponentDemand(Guid JobId, string JobNumber, Guid MaterialId, DateTimeOffset? NeededFrom, decimal Remaining);

/// <summary>Supply on its way (D-058), in the component's base unit, due at <see cref="ExpectedAt"/>.</summary>
public sealed record IncomingSupply(Guid MaterialId, DateTimeOffset ExpectedAt, decimal Quantity);

/// <summary>
/// A job's demand in turn: <see cref="Cumulative"/> is what this and every earlier job need in total. Of its own need,
/// <see cref="FromStock"/> comes from available stock, <see cref="FromIncoming"/> from supply due by then, and
/// <see cref="Uncovered"/> from neither.
/// </summary>
public sealed record ShortageLine(ComponentDemand Demand, decimal Cumulative, decimal Uncovered, decimal FromStock = 0m, decimal FromIncoming = 0m);

/// <summary>One component's demand against its available stock and incoming supply.</summary>
public sealed record MaterialShortage(Guid MaterialId, decimal Available, decimal Demand, decimal Shortfall, IReadOnlyList<ShortageLine> Lines, decimal Incoming = 0m)
{
    /// <summary>The first job, in the order they are needed, that comes up short; null when supply covers them all.</summary>
    public ShortageLine? FirstShort => Lines.FirstOrDefault(l => l.Uncovered > 0);
}

/// <summary>
/// Sets open jobs' remaining components against available stock and, when given, supply on its way (D-029, D-058): jobs take
/// supply in the order they are needed, stock first, then what is due by the time they need it, so the first ones are covered
/// and the shortfall lands on the later ones. Supply arriving after a job needs it doesn't cover that job, however much
/// arrives. Pure and recalculated on every read. Not MRP: nothing is suggested, ordered or rescheduled.
/// </summary>
public static class ShortageCalculator
{
    /// <param name="demand">Remaining needs; lines with nothing left are ignored.</param>
    /// <param name="available">Available stock per material in its base unit; a missing material has none.</param>
    /// <param name="incoming">Supply on its way; a job without a time can use all of it.</param>
    /// <param name="now">
    /// When given, times before it count as now when supply is set against need: a job already under way needs what is left from
    /// now, and a late delivery can arrive from now. The order of the jobs isn't changed.
    /// </param>
    public static IReadOnlyList<MaterialShortage> Calculate(
        IEnumerable<ComponentDemand> demand, IReadOnlyDictionary<Guid, decimal> available, IEnumerable<IncomingSupply>? incoming = null, DateTimeOffset? now = null)
    {
        var supply = (incoming ?? []).ToLookup(i => i.MaterialId);
        return [.. demand
            .Where(d => d.Remaining > 0)
            .GroupBy(d => d.MaterialId)
            .Select(g => ForMaterial(g.Key, g, available.GetValueOrDefault(g.Key), [.. supply[g.Key].OrderBy(i => i.ExpectedAt)], now))];
    }

    private static MaterialShortage ForMaterial(
        Guid materialId, IEnumerable<ComponentDemand> demand, decimal available, IReadOnlyList<IncomingSupply> incoming, DateTimeOffset? now)
    {
        DateTimeOffset NotBefore(DateTimeOffset at) => now is { } n && at < n ? n : at;

        var lines = new List<ShortageLine>();
        var cumulative = 0m;
        var stock = available;
        var arrived = 0m;
        var next = 0;
        foreach (var need in demand
            .OrderBy(d => d.NeededFrom is null)
            .ThenBy(d => d.NeededFrom)
            .ThenBy(d => d.JobNumber, StringComparer.Ordinal))
        {
            // What has come in by the time this job needs it (all of it, for a job with no time).
            while (next < incoming.Count && (need.NeededFrom is not { } at || NotBefore(incoming[next].ExpectedAt) <= NotBefore(at)))
            {
                arrived += incoming[next++].Quantity;
            }

            cumulative += need.Remaining;
            var fromStock = Math.Min(stock, need.Remaining);
            stock -= fromStock;
            var fromIncoming = Math.Min(arrived, need.Remaining - fromStock);
            arrived -= fromIncoming;
            lines.Add(new ShortageLine(need, cumulative, need.Remaining - fromStock - fromIncoming, fromStock, fromIncoming));
        }

        return new MaterialShortage(materialId, available, cumulative, lines.Sum(l => l.Uncovered), lines, incoming.Sum(i => i.Quantity));
    }
}
