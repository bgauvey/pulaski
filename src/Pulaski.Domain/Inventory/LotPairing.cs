namespace Pulaski.Domain.Inventory;

/// <summary>A standing consumption or production declared against a job, in the order it counts (D-057).</summary>
public sealed record JobDeclaration(Guid JobId, Guid LotId, Guid MaterialId, bool Produced, DateTimeOffset At, DateTimeOffset RecordedAt);

/// <summary>A lot the job used paired with a lot it made, how sure that is, and why.</summary>
public sealed record LotPair(Guid JobId, Guid ParentLotId, Guid ChildLotId, string Precision, string Reason);

/// <summary>
/// Which of the lots a job used each lot it made came from (#81), by when they were declared. A lot the job used is in use from its
/// consumption until the next lot of that material, so what is made pairs with the lot in use when the previous output was declared and
/// every lot of the material declared since. A lot used after everything was made, or a material none of whose lots came before an
/// output, can't be paired by time and keeps its job link as possible. Pure.
/// </summary>
public static class LotPairing
{
    public const string PairedReason = "Paired by time: it was in use on the job while this was made.";
    public const string OnlyLotReason = "The job used only one lot of this material.";
    public const string UsedAfterReason = "Declared after everything the job made, so it can't be paired by time.";
    public const string NothingBeforeReason = "No lot of this material was declared before it was made, so it can't be paired by time.";

    public static IReadOnlyList<LotPair> Pair(IEnumerable<JobDeclaration> declarations) =>
        [.. declarations.GroupBy(d => d.JobId).SelectMany(PairJob)];

    private static IEnumerable<LotPair> PairJob(IGrouping<Guid, JobDeclaration> job)
    {
        var ordered = job.OrderBy(d => d.At).ThenBy(d => d.RecordedAt).ToList();
        var materials = ordered.Where(d => !d.Produced).GroupBy(d => d.MaterialId).ToDictionary(g => g.Key, g => g.ToList());
        var made = ordered.Where(d => d.Produced).ToList();
        var pairs = new Dictionary<(Guid Parent, Guid Child), (string Precision, string Reason)>();

        DateTimeOffset? start = null;
        foreach (var output in made)
        {
            foreach (var used in materials.Values)
            {
                var inUse = used.Where(c => (start is null || c.At > start) && c.At <= output.At).ToList();
                if (start is { } from && used.LastOrDefault(c => c.At <= from) is { } running)
                {
                    inUse.Add(running);
                }

                var only = used.Select(c => c.LotId).Distinct().Count() == 1;
                foreach (var lot in inUse.Select(c => c.LotId).Distinct())
                {
                    Add(lot, output.LotId, only ? GenealogyPrecision.Exact : GenealogyPrecision.Estimated, only ? OnlyLotReason : PairedReason);
                }
            }

            start = output.At;
        }

        // What can't be paired by time keeps its job link, as possible.
        var paired = pairs.Keys.Select(k => k.Parent).ToHashSet();
        foreach (var lot in materials.Values.SelectMany(u => u).Select(c => c.LotId).Distinct().Where(l => !paired.Contains(l)))
        {
            foreach (var child in made.Select(p => p.LotId).Distinct())
            {
                Add(lot, child, GenealogyPrecision.Possible, UsedAfterReason);
            }
        }

        foreach (var child in made.Select(p => p.LotId).Distinct())
        {
            foreach (var used in materials.Values)
            {
                var lots = used.Select(c => c.LotId).Distinct().ToList();
                if (!lots.Any(l => pairs.ContainsKey((l, child))))
                {
                    foreach (var lot in lots)
                    {
                        Add(lot, child, GenealogyPrecision.Possible, NothingBeforeReason);
                    }
                }
            }
        }

        return pairs.Select(p => new LotPair(job.Key, p.Key.Parent, p.Key.Child, p.Value.Precision, p.Value.Reason));

        void Add(Guid parent, Guid child, string precision, string reason)
        {
            if (parent == child)
            {
                return;
            }

            if (!pairs.TryGetValue((parent, child), out var existing) || GenealogyPrecision.Best(existing.Precision, precision) != existing.Precision)
            {
                pairs[(parent, child)] = (precision, reason);
            }
        }
    }
}
