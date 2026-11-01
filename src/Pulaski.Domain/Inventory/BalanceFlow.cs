namespace Pulaski.Domain.Inventory;

/// <summary>How stock flows through a location whose lots mix (D-031, D-060).</summary>
public static class FlowModels
{
    /// <summary>Everything in it blends, as in a tank or a mixed silo: a withdrawal takes from every arrival still there, by share.</summary>
    public const string Mixed = "mixed";

    /// <summary>Plug flow, as in a mass-flow silo or bin: a withdrawal takes the oldest material first.</summary>
    public const string Fifo = "fifo";

    public static IReadOnlyList<string> All { get; } = [Mixed, Fifo];
}

/// <summary>How sure a genealogy link is: through a balance (D-060), or between what a job used and what it made (#81).</summary>
public static class GenealogyPrecision
{
    /// <summary>Exact from the declared movements, and a level reading backs it: the balance was measured empty, or held what was declared.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Only one arrival could have been there, or the job used only one lot of the material: certain from the declared movements.</summary>
    public const string Exact = "exact";

    /// <summary>Someone named the lots it was made from.</summary>
    public const string Declared = "declared";

    /// <summary>Worked out by the flow model, or by when lots were used and made: likely, not measured.</summary>
    public const string Estimated = "estimated";

    /// <summary>The declared movements don't settle it (they don't add up, the residence time wasn't met, or a level disagrees): it may be, or not.</summary>
    public const string Possible = "possible";

    /// <summary>The least certain of two.</summary>
    public static string Worst(string a, string b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>The most certain of two.</summary>
    public static string Best(string a, string b) => Rank(a) <= Rank(b) ? a : b;

    /// <summary>The least certain of several; exact when there are none.</summary>
    public static string WorstOf(IEnumerable<string> precisions) =>
        precisions.Aggregate((string?)null, (worst, p) => worst is null ? p : Worst(worst, p)) ?? Exact;

    private static int Rank(string precision) => precision switch
    {
        Confirmed => 0,
        Exact => 1,
        Declared => 2,
        Estimated => 3,
        _ => 4,
    };
}

/// <summary>
/// One quantity movement of a balance, in the order it counts (D-057): an arrival (positive; from <see cref="SourceLotId"/> when a
/// lot was merged in, else stock with no lot of its own) or a withdrawal (negative; into <see cref="JobId"/> for consumption).
/// </summary>
public sealed record BalanceMovement(Guid EventId, DateTimeOffset At, decimal Delta, Guid? SourceLotId, Guid? JobId, string Kind);

/// <summary>What one arrival gave a withdrawal; a null <see cref="SourceLotId"/> is stock with no lot of its own.</summary>
public sealed record Contribution(Guid? SourceLotId, Guid? SourceEventId, decimal Quantity, string Precision);

/// <summary>Where one withdrawal's stock came from, how sure that is, and why.</summary>
public sealed record WithdrawalAllocation(BalanceMovement Withdrawal, IReadOnlyList<Contribution> Sources, string Precision, string Reason);

/// <summary>A level reading at or below empty (#81): from <see cref="At"/> the balance was measured empty of what arrived before <see cref="ClearsBefore"/>,
/// the last reading above empty, so an arrival the level hadn't caught up with yet isn't cleared.</summary>
public sealed record EmptyPoint(DateTimeOffset At, DateTimeOffset ClearsBefore);

/// <summary>
/// What the level readings of a balance's location say (#81): when it was measured empty, and, when the level's unit converts to the
/// material's, the stock it read just before each withdrawal (by withdrawal event, in the base unit), compared within <see cref="Tolerance"/>.
/// </summary>
public sealed record BalanceLevels(IReadOnlyList<EmptyPoint> Empties, IReadOnlyDictionary<Guid, decimal> StockBefore, decimal? Tolerance)
{
    public static BalanceLevels None { get; } = new([], new Dictionary<Guid, decimal>(), null);
}

/// <summary>
/// Which arrivals into a balance fed each withdrawal from it (D-060), worked out from the declared movements. A mixed balance
/// gives every withdrawal a share of each arrival still in it; a first-in, first-out one takes the oldest first, and with a minimum
/// residence time only material that has been in that long. It never claims more than the movements show: a withdrawal is exact
/// only when one arrival could have been there, and possible when the declared quantities don't account for it. Level readings (#81)
/// clear what a measured empty shows was gone, confirm an exact withdrawal the level backs, and make one it contradicts possible. Pure.
/// </summary>
public static class BalanceAllocation
{
    private const int Decimals = 6;

    public static IReadOnlyList<WithdrawalAllocation> Allocate(
        IEnumerable<BalanceMovement> movements, string model, TimeSpan? minResidence, BalanceLevels? levels = null)
    {
        levels ??= BalanceLevels.None;
        var empties = new Queue<EmptyPoint>(levels.Empties.OrderBy(e => e.At));
        var parcels = new List<Parcel>();
        var result = new List<WithdrawalAllocation>();
        var measuredEmpty = false;
        string? note = null;
        foreach (var movement in movements.OrderBy(m => m.At))
        {
            while (empties.TryPeek(out var empty) && empty.At < movement.At)
            {
                empties.Dequeue();
                var cleared = parcels.Where(p => p.ArrivedAt < empty.ClearsBefore).ToList();
                var left = cleared.Sum(p => p.Remaining);
                parcels.RemoveAll(cleared.Contains);
                measuredEmpty = parcels.Count == 0;
                if (left > (levels.Tolerance ?? 0m))
                {
                    note = $" The level read empty at {empty.At:u} while the declared movements still had {left:0.######} in; that was set aside.";
                }
            }

            if (movement.Delta > 0)
            {
                parcels.Add(new Parcel(movement.SourceLotId, movement.EventId, movement.At, movement.Delta));
                continue;
            }

            if (movement.Delta < 0)
            {
                var declared = parcels.Sum(p => p.Remaining);
                var single = parcels.Count == 1;
                var allocation = model == FlowModels.Fifo ? TakeFifo(parcels, movement, minResidence) : TakeMixed(parcels, movement);
                if (note is not null)
                {
                    allocation = allocation with { Reason = allocation.Reason + note };
                    note = null;
                }

                if (levels.Tolerance is { } tolerance && levels.StockBefore.TryGetValue(movement.EventId, out var level))
                {
                    allocation = Math.Abs(level - declared) <= tolerance
                        ? Upgrade(allocation, $" The level read {level:0.######}, agreeing with the declared {declared:0.######}.")
                        : Downgrade(allocation, $" The level read {level:0.######} but the declared movements had {declared:0.######}: they disagree, so this is only possible.");
                }
                else if (measuredEmpty && single)
                {
                    allocation = Upgrade(allocation, " The level showed the balance empty before it arrived.");
                }

                result.Add(allocation);
                parcels.RemoveAll(p => p.Remaining <= 0);
                if (parcels.Count == 0)
                {
                    measuredEmpty = false;
                }
            }
        }

        return result;
    }

    /// <summary>Exact sources become confirmed; anything less certain stays as it was.</summary>
    private static WithdrawalAllocation Upgrade(WithdrawalAllocation allocation, string reason)
    {
        var sources = allocation.Sources.Select(s => s.Precision == GenealogyPrecision.Exact ? s with { Precision = GenealogyPrecision.Confirmed } : s).ToList();
        return allocation with { Sources = sources, Precision = GenealogyPrecision.WorstOf(sources.Select(s => s.Precision)), Reason = allocation.Reason + reason };
    }

    private static WithdrawalAllocation Downgrade(WithdrawalAllocation allocation, string reason) => allocation with
    {
        Sources = [.. allocation.Sources.Select(s => s with { Precision = GenealogyPrecision.Possible })],
        Precision = GenealogyPrecision.Possible,
        Reason = allocation.Reason + reason,
    };

    private static WithdrawalAllocation TakeMixed(List<Parcel> parcels, BalanceMovement withdrawal)
    {
        var need = -withdrawal.Delta;
        var total = parcels.Sum(p => p.Remaining);
        if (total <= 0)
        {
            return Unaccounted(withdrawal, need, "The declared movements had nothing in the balance when this was taken.");
        }

        var taken = Math.Min(need, total);
        var sources = new List<Contribution>();
        var single = parcels.Count == 1;
        foreach (var parcel in parcels)
        {
            var share = Math.Round(taken * parcel.Remaining / total, Decimals, MidpointRounding.AwayFromZero);
            parcel.Remaining -= share;
            sources.Add(new Contribution(parcel.SourceLotId, parcel.EventId, share, single ? GenealogyPrecision.Exact : GenealogyPrecision.Estimated));
        }

        return Finish(withdrawal, sources, need - taken,
            single ? "Only one arrival was in the balance." : "Mixed: each arrival still in the balance gives its share by quantity.");
    }

    private static WithdrawalAllocation TakeFifo(List<Parcel> parcels, BalanceMovement withdrawal, TimeSpan? minResidence)
    {
        var need = -withdrawal.Delta;
        if (parcels.Sum(p => p.Remaining) <= 0)
        {
            return Unaccounted(withdrawal, need, "The declared movements had nothing in the balance when this was taken.");
        }

        var single = parcels.Count == 1;
        var sources = new List<Contribution>();
        var early = false;

        // Oldest first among what has stayed long enough; then, if that isn't enough, what arrived too recently to leave yet.
        foreach (var ready in new[] { true, false })
        {
            foreach (var parcel in parcels.Where(p => Ready(p, withdrawal.At, minResidence) == ready))
            {
                if (need <= 0)
                {
                    break;
                }

                var take = Math.Min(need, parcel.Remaining);
                parcel.Remaining -= take;
                need -= take;
                early |= !ready;
                sources.Add(new Contribution(parcel.SourceLotId, parcel.EventId, take,
                    !ready ? GenealogyPrecision.Possible : single ? GenealogyPrecision.Exact : GenealogyPrecision.Estimated));
            }
        }

        return Finish(withdrawal, sources, need,
            early ? "First in, first out, but some of it would have left before the minimum residence time: those arrivals are only possible."
            : single ? "Only one arrival was in the balance."
            : "First in, first out: the oldest material in the balance left first.");
    }

    private static bool Ready(Parcel parcel, DateTimeOffset at, TimeSpan? minResidence) => minResidence is not { } residence || parcel.ArrivedAt + residence <= at;

    private static WithdrawalAllocation Finish(BalanceMovement withdrawal, List<Contribution> sources, decimal unaccounted, string reason)
    {
        if (unaccounted > 0)
        {
            sources.Add(new Contribution(null, null, unaccounted, GenealogyPrecision.Possible));
            reason += $" More was taken than the declared movements put in; where {unaccounted:0.######} came from isn't known.";
        }

        return new WithdrawalAllocation(withdrawal, sources, GenealogyPrecision.WorstOf(sources.Select(s => s.Precision)), reason);
    }

    private static WithdrawalAllocation Unaccounted(BalanceMovement withdrawal, decimal need, string reason) =>
        new(withdrawal, [new Contribution(null, null, need, GenealogyPrecision.Possible)], GenealogyPrecision.Possible, reason);

    private sealed class Parcel(Guid? sourceLotId, Guid eventId, DateTimeOffset arrivedAt, decimal remaining)
    {
        public Guid? SourceLotId { get; } = sourceLotId;

        public Guid EventId { get; } = eventId;

        public DateTimeOffset ArrivedAt { get; } = arrivedAt;

        public decimal Remaining { get; set; } = remaining;
    }
}
