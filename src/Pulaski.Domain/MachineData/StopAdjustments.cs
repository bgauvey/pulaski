namespace Pulaski.Domain.MachineData;

/// <summary>Where a stop came from (D-069): values of <c>ts.downtime.source</c>.</summary>
public static class StopSources
{
    /// <summary>Opened and closed by machine data (D-050).</summary>
    public const string Machine = "machine";

    /// <summary>Entered by hand, for time the machine didn't report.</summary>
    public const string Manual = "manual";

    /// <summary>One part of a stop that was split.</summary>
    public const string Split = "split";

    /// <summary>Stops merged into one.</summary>
    public const string Merge = "merge";
}

/// <summary>A stop's time, as adjustments check it. An open stop has no end.</summary>
public sealed record StopInterval(Guid Id, DateTimeOffset Start, DateTimeOffset? End);

/// <summary>
/// Adjusting stops (D-069): entering one by hand, splitting one in two, merging several into one. A stop adjusted is never changed:
/// it is superseded and the new stops replace it, so a machine's current stops never overlap one another. Only closed stops are split
/// or merged, and only within <see cref="MaxAge"/>; open stops belong to machine data until they close.
/// </summary>
public static class StopAdjustments
{
    /// <summary>How far back stops may be entered, split or merged.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(31);

    /// <summary>The first of <paramref name="others"/> overlapping <c>[start, end)</c>, if any; an open stop lasts for ever.</summary>
    public static StopInterval? FirstOverlap(DateTimeOffset start, DateTimeOffset end, IEnumerable<StopInterval> others) =>
        others.Where(o => o.Start < end && (o.End is not { } e || e > start)).OrderBy(o => o.Start).FirstOrDefault();

    /// <summary>A stop entered by hand: closed, in the past, within <see cref="MaxAge"/>, and overlapping none of the machine's current stops.</summary>
    /// <param name="start">When it started.</param>
    /// <param name="end">When it ended.</param>
    /// <param name="now">Now.</param>
    /// <param name="current">The machine's current (not superseded) stops around that time.</param>
    public static void EnsureCanEnter(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now, IEnumerable<StopInterval> current)
    {
        if (end <= start)
        {
            throw new DomainRuleException("A stop must end after it starts.");
        }

        if (end > now)
        {
            throw new DomainRuleException("A stop entered by hand must have ended: it can't end in the future.");
        }

        EnsureRecent(start, now);
        if (FirstOverlap(start, end, current) is { } overlap)
        {
            throw new DomainRuleException(Overlapping(overlap));
        }
    }

    /// <summary>A split: the stop is closed, recent and current, and the time falls strictly inside it.</summary>
    public static void EnsureCanSplit(StopInterval stop, bool isSuperseded, DateTimeOffset at, DateTimeOffset now)
    {
        EnsureAdjustable(stop, isSuperseded, now);
        if (at <= stop.Start || at >= stop.End)
        {
            throw new DomainRuleException("Split a stop at a time inside it, after its start and before its end.");
        }
    }

    /// <summary>
    /// A merge: two or more closed, recent, current stops of one machine, with no other current stop of it between them. The time
    /// between them, even when the machine ran, becomes part of the merged stop.
    /// </summary>
    /// <param name="stops">The stops to merge, with whether each is superseded.</param>
    /// <param name="current">The machine's current stops between the first one's start and the last one's end.</param>
    /// <param name="now">Now.</param>
    /// <returns>The merged stop's start and end.</returns>
    public static (DateTimeOffset Start, DateTimeOffset End) EnsureCanMerge(
        IReadOnlyList<(StopInterval Stop, bool IsSuperseded)> stops, IEnumerable<StopInterval> current, DateTimeOffset now)
    {
        if (stops.Count < 2)
        {
            throw new DomainRuleException("Choose at least two stops to merge.");
        }

        foreach (var (stop, superseded) in stops)
        {
            EnsureAdjustable(stop, superseded, now);
        }

        var start = stops.Min(s => s.Stop.Start);
        var end = stops.Max(s => s.Stop.End!.Value);
        var chosen = stops.Select(s => s.Stop.Id).ToHashSet();
        if (FirstOverlap(start, end, current.Where(c => !chosen.Contains(c.Id))) is { } between)
        {
            throw new DomainRuleException($"Only consecutive stops can be merged: another stop, at {between.Start:u}, lies between them.");
        }

        return (start, end);
    }

    private static void EnsureAdjustable(StopInterval stop, bool isSuperseded, DateTimeOffset now)
    {
        if (isSuperseded)
        {
            throw new DomainRuleException($"The stop at {stop.Start:u} has already been split or merged; adjust the stops that replaced it.");
        }

        if (stop.End is null)
        {
            throw new DomainRuleException($"The stop at {stop.Start:u} is still open: adjust it once the machine runs again.");
        }

        EnsureRecent(stop.Start, now);
    }

    private static void EnsureRecent(DateTimeOffset start, DateTimeOffset now)
    {
        if (now - start > MaxAge)
        {
            throw new DomainRuleException($"Stops can be adjusted for {MaxAge.TotalDays:0} days after they start.");
        }
    }

    private static string Overlapping(StopInterval overlap) =>
        overlap.End is { } end
            ? $"It overlaps the stop from {overlap.Start:u} to {end:u}; split or merge that one instead."
            : $"It overlaps the stop open since {overlap.Start:u}.";
}
