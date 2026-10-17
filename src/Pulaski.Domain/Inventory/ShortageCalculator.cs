namespace Pulaski.Domain.Inventory;

/// <summary>
/// What one job still needs of one component, in the component's base unit (D-029).
/// <see cref="NeededFrom"/> orders the jobs; null means the job has no time yet and comes last.
/// </summary>
public sealed record ComponentDemand(Guid JobId, string JobNumber, Guid MaterialId, DateTimeOffset? NeededFrom, decimal Remaining);

/// <summary>
/// A job's demand in turn: <see cref="Cumulative"/> is what this and every earlier job need in
/// total, and <see cref="Uncovered"/> is the part of this job's need that available stock doesn't cover.
/// </summary>
public sealed record ShortageLine(ComponentDemand Demand, decimal Cumulative, decimal Uncovered);

/// <summary>One component's demand against its available stock.</summary>
public sealed record MaterialShortage(Guid MaterialId, decimal Available, decimal Demand, decimal Shortfall, IReadOnlyList<ShortageLine> Lines)
{
    /// <summary>The first job, in the order they are needed, that comes up short; null when stock covers them all.</summary>
    public ShortageLine? FirstShort => Lines.FirstOrDefault(l => l.Uncovered > 0);
}

/// <summary>
/// Sets open jobs' remaining components against available stock (D-029): jobs take stock in the
/// order they are needed, so the first ones are covered and the shortfall lands on the later ones.
/// Pure and recalculated on every read. Not MRP: nothing is suggested, and no incoming supply counted.
/// </summary>
public static class ShortageCalculator
{
    /// <param name="demand">Remaining needs; lines with nothing left are ignored.</param>
    /// <param name="available">Available stock per material in its base unit; a missing material has none.</param>
    public static IReadOnlyList<MaterialShortage> Calculate(IEnumerable<ComponentDemand> demand, IReadOnlyDictionary<Guid, decimal> available) =>
        [.. demand
            .Where(d => d.Remaining > 0)
            .GroupBy(d => d.MaterialId)
            .Select(g => ForMaterial(g.Key, g, available.GetValueOrDefault(g.Key)))];

    private static MaterialShortage ForMaterial(Guid materialId, IEnumerable<ComponentDemand> demand, decimal available)
    {
        var lines = new List<ShortageLine>();
        var cumulative = 0m;
        var uncoveredBefore = 0m;
        foreach (var need in demand
            .OrderBy(d => d.NeededFrom is null)
            .ThenBy(d => d.NeededFrom)
            .ThenBy(d => d.JobNumber, StringComparer.Ordinal))
        {
            cumulative += need.Remaining;
            var uncovered = Math.Max(0m, cumulative - available);
            lines.Add(new ShortageLine(need, cumulative, uncovered - uncoveredBefore));
            uncoveredBefore = uncovered;
        }

        return new MaterialShortage(materialId, available, cumulative, Math.Max(0m, cumulative - available), lines);
    }
}
