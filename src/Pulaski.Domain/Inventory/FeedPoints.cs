namespace Pulaski.Domain.Inventory;

/// <summary>Where an automated consumption came from (#33). Values match <c>ts.lot_event.auto_source</c> and <c>core.feed_shortfall.source</c>.</summary>
public static class AutoSource
{
    /// <summary>A production report took the component's quantity per unit made from the lots at the line's feed points.</summary>
    public const string Backflush = "backflush";

    /// <summary>A production report shared the component's quantity among the lots loaded while it was made.</summary>
    public const string TimeWindow = "time_window";

    /// <summary>A feed point's consumed counter: each counted amount out of the lot loaded then.</summary>
    public const string Counter = "counter";

    public static IReadOnlyList<string> All { get; } = [Backflush, TimeWindow, Counter];
}

/// <summary>A standing movement of a lot that names a feed point, in the order it counts (D-057).</summary>
public sealed record FeedEvent(
    Guid LotId, Guid MaterialId, DateTimeOffset At, DateTimeOffset RecordedAt, string Kind, decimal? QtyDelta, decimal QtyAfter, Guid? FromLocationId, Guid? ToLocationId);

/// <summary>A lot loaded at a feed point from <see cref="LoadedAt"/> until <see cref="EndedAt"/> (null: still loaded).</summary>
public sealed record FeedLoad(Guid FeedPointId, Guid LotId, Guid MaterialId, DateTimeOffset LoadedAt, DateTimeOffset? EndedAt)
{
    public bool Covers(DateTimeOffset at) => LoadedAt <= at && (EndedAt is null || at < EndedAt);

    /// <summary>How long it was loaded within <c>[from, to)</c>.</summary>
    public TimeSpan Overlap(DateTimeOffset from, DateTimeOffset to)
    {
        var start = LoadedAt > from ? LoadedAt : from;
        var end = EndedAt is { } ended && ended < to ? ended : to;
        return end > start ? end - start : TimeSpan.Zero;
    }
}

/// <summary>What one lot gives a time-window consumption.</summary>
public sealed record FeedTake(Guid LotId, decimal Quantity);

/// <summary>
/// Lots loaded at feed points (#33), worked out from their movements: a lot is loaded when it arrives at the feed point (received, moved,
/// split or made there) or is loaded there again, and stays loaded until it leaves, runs out, or the next lot of its material is loaded
/// there. Pure.
/// </summary>
public static class FeedLoads
{
    public static IReadOnlyList<FeedLoad> From(Guid feedPointId, IEnumerable<FeedEvent> events)
    {
        var spans = new List<FeedLoad>();
        foreach (var lot in events.GroupBy(e => e.LotId))
        {
            DateTimeOffset? since = null;
            var material = lot.First().MaterialId;
            foreach (var e in lot.OrderBy(e => e.At).ThenBy(e => e.RecordedAt))
            {
                var leaves = e.QtyDelta is null && e.FromLocationId == feedPointId && e.ToLocationId is { } to && to != feedPointId;
                if (since is { } start && (leaves || (e.QtyDelta < 0 && e.QtyAfter <= 0)))
                {
                    spans.Add(new FeedLoad(feedPointId, lot.Key, material, start, e.At));
                    since = null;
                    continue;
                }

                if (e.ToLocationId == feedPointId && !leaves && (since is null || e.Kind == LotEventKind.Loaded))
                {
                    if (since is { } earlier)
                    {
                        spans.Add(new FeedLoad(feedPointId, lot.Key, material, earlier, e.At));
                    }

                    since = e.At;
                }
            }

            if (since is { } open)
            {
                spans.Add(new FeedLoad(feedPointId, lot.Key, material, open, null));
            }
        }

        // The next lot of a material loaded there ends the one before.
        var result = new List<FeedLoad>();
        foreach (var material in spans.GroupBy(s => s.MaterialId))
        {
            var ordered = material.OrderBy(s => s.LoadedAt).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var next = ordered.Skip(i + 1).FirstOrDefault(s => s.LoadedAt > ordered[i].LoadedAt)?.LoadedAt;
                var end = ordered[i].EndedAt is { } ended && (next is null || ended < next) ? ended : next ?? ordered[i].EndedAt;
                result.Add(ordered[i] with { EndedAt = end });
            }
        }

        return [.. result.OrderBy(l => l.LoadedAt)];
    }

    /// <summary>
    /// The quantity shared among the loads of the material in <c>[from, to]</c>, by how long each was loaded then; a report with no time
    /// span goes to what was loaded at its time. Empty when nothing was loaded.
    /// </summary>
    public static IReadOnlyList<FeedTake> ShareByTime(IEnumerable<FeedLoad> loads, Guid materialId, DateTimeOffset from, DateTimeOffset to, decimal quantity)
    {
        var mine = loads.Where(l => l.MaterialId == materialId).ToList();
        var overlaps = mine.Select(l => (l.LotId, Span: l.Overlap(from, to))).Where(o => o.Span > TimeSpan.Zero).ToList();
        if (overlaps.Count == 0)
        {
            return mine.Where(l => l.Covers(to)).Take(1).Select(l => new FeedTake(l.LotId, quantity)).ToList();
        }

        var total = overlaps.Sum(o => o.Span.Ticks);
        var takes = overlaps.GroupBy(o => o.LotId)
            .Select(g => new FeedTake(g.Key, Math.Round(quantity * g.Sum(o => o.Span.Ticks) / total, 6, MidpointRounding.AwayFromZero)))
            .ToList();

        // Rounding goes to the largest share, so the shares add up.
        var rest = quantity - takes.Sum(t => t.Quantity);
        if (rest != 0)
        {
            var largest = takes.OrderByDescending(t => t.Quantity).First();
            takes[takes.IndexOf(largest)] = largest with { Quantity = largest.Quantity + rest };
        }

        return takes;
    }
}
