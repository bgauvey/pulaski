namespace Pulaski.Domain.Inventory;

/// <summary>One recorded movement of a lot, as its state at a past time is worked out from (<c>ts.lot_event</c>).</summary>
public sealed record PastEvent(
    Guid Id,
    DateTimeOffset EffectiveAt,
    DateTimeOffset OccurredAt,
    string Kind,
    decimal? QtyDelta,
    Guid? FromLocationId,
    Guid? ToLocationId,
    string? FromStatus,
    string? ToStatus,
    Guid? ContainerId)
{
    /// <summary>
    /// The whole lot changed location: a move or a pack with its container, with no quantity of its own. Rows with a quantity (a transfer
    /// between balances, a split, a merge, a receipt or a take) name locations but never move the lot itself.
    /// </summary>
    public bool Relocates => QtyDelta is null && FromLocationId is not null && ToLocationId is not null;

    /// <summary>Changed the lot's status: the status before it is <see cref="FromStatus"/>.</summary>
    public bool ChangesStatus => FromStatus is not null && ToStatus is not null;
}

/// <summary>What a lot is now, as its past is worked back from.</summary>
public sealed record LotNow(decimal Quantity, Guid? LocationId, string Status, bool IsBulk)
{
    public static LotNow Of(Lot lot) => new(lot.Quantity, lot.LocationId, lot.Status, lot.IsBulk);
}

/// <summary>A lot as it was at a time: whether it existed, its quantity, location, status and container then.</summary>
public sealed record LotStateAt(bool Existed, decimal Quantity, Guid? LocationId, string Status, Guid? ContainerId);

/// <summary>
/// A lot's state at a past time (#77, D-076), in the order movements count (<c>effective_at</c>, then record order). Quantity, location and
/// status are worked back from the lot as it is now, undoing every movement that counts after the time; the container is replayed forward,
/// since emptying a lot clears it without a record. So the state at any time agrees with the journal by construction.
/// </summary>
public static class LotPastState
{
    /// <param name="current">The lot now.</param>
    /// <param name="events">Its movements; those counting after <paramref name="at"/> are enough for everything but the container.</param>
    /// <param name="at">The time.</param>
    /// <param name="existed">True when the caller already knows the lot existed then (it passes only the movements after it).</param>
    public static LotStateAt At(LotNow current, IEnumerable<PastEvent> events, DateTimeOffset at, bool existed = false)
    {
        var ordered = events.OrderBy(e => e.EffectiveAt).ThenBy(e => e.OccurredAt).ToList();
        var quantity = current.Quantity;
        var location = current.LocationId;
        var status = current.Status;
        for (var i = ordered.Count - 1; i >= 0 && ordered[i].EffectiveAt > at; i--)
        {
            var e = ordered[i];
            quantity -= e.QtyDelta ?? 0m;
            if (e.ChangesStatus)
            {
                status = e.FromStatus!;
            }

            if (e.Relocates)
            {
                location = e.FromLocationId;
            }
        }

        existed = existed || (ordered.Count > 0 && ordered[0].EffectiveAt <= at);
        return new LotStateAt(existed, existed ? quantity : 0m, location, status, existed ? ContainerAt(current, ordered, at) : null);
    }

    /// <summary>
    /// Why a movement of the lot can't count from <paramref name="at"/> because another counts after it, or null. Moves, holds and releases,
    /// and a merge's emptied lots, must be the lot's latest movement: what happened after names where the lot was and needs its status.
    /// </summary>
    public static PastEvent? CountsAfter(IEnumerable<PastEvent> events, DateTimeOffset at) =>
        events.Where(e => e.EffectiveAt > at).OrderBy(e => e.EffectiveAt).ThenBy(e => e.OccurredAt).FirstOrDefault();

    /// <summary>The first movement after <paramref name="at"/> that changed the lot's status or location; null when none did.</summary>
    public static PastEvent? ChangedAfter(IEnumerable<PastEvent> events, DateTimeOffset at) =>
        events.Where(e => e.EffectiveAt > at && (e.ChangesStatus || e.Relocates || e.Kind is LotEventKind.Packed or LotEventKind.Unpacked))
            .OrderBy(e => e.EffectiveAt).ThenBy(e => e.OccurredAt).FirstOrDefault();

    private static Guid? ContainerAt(LotNow current, List<PastEvent> ordered, DateTimeOffset at)
    {
        Guid? container = null;
        var quantity = 0m;
        foreach (var e in ordered.TakeWhile(e => e.EffectiveAt <= at))
        {
            quantity += e.QtyDelta ?? 0m;
            container = e.Kind switch
            {
                LotEventKind.Packed => e.ContainerId,
                LotEventKind.Unpacked => null,
                _ when !current.IsBulk && e.QtyDelta < 0 && quantity <= 0 => null,
                _ => container,
            };
        }

        return container;
    }
}
