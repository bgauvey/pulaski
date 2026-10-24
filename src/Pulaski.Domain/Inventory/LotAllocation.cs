using System.Globalization;

namespace Pulaski.Domain.Inventory;

/// <summary>Values of <c>core.lot_allocation.status</c> (D-064).</summary>
public static class AllocationStatus
{
    public const string Active = "active";

    /// <summary>Used up by consumption into its job.</summary>
    public const string Consumed = "consumed";

    public const string Released = "released";
}

/// <summary>Values of <c>core.lot_allocation.basis</c>: picked by a person, or taken from the recommendation.</summary>
public static class AllocationBasis
{
    public const string Chosen = "chosen";
    public const string Recommended = "recommended";
}

/// <summary>
/// Stock of one lot set aside for one of a job's components (D-064), in the lot's base unit. While active, other jobs see it as taken;
/// consuming the lot into its job uses it up. It never changes the lot: final consumption stays under the inventory rules (D-027).
/// </summary>
public sealed record LotAllocation(
    Guid Id,
    Guid SiteId,
    Guid JobId,
    Guid MaterialId,
    Guid LotId,
    decimal Quantity,
    decimal AllocatedQty,
    string Status,
    string Basis,
    string? Note = null)
{
    public bool IsActive => Status == AllocationStatus.Active;

    /// <summary>Sets more of the same lot aside for the same job.</summary>
    public LotAllocation TopUp(decimal quantity) => this with { Quantity = Quantity + quantity, AllocatedQty = AllocatedQty + quantity };

    /// <summary>Consumption of the lot into its job: uses up to <paramref name="consumed"/> of it, and the whole of it ends it.</summary>
    public (LotAllocation Allocation, decimal Used) UseUp(decimal consumed)
    {
        var used = Math.Min(consumed, Quantity);
        var left = Quantity - used;
        return (this with { Quantity = left, Status = left == 0 ? AllocationStatus.Consumed : Status }, used);
    }

    /// <summary>Gives the stock back to everyone, saying why.</summary>
    /// <exception cref="DomainRuleException">It isn't active.</exception>
    public LotAllocation Release(string? note) => IsActive
        ? this with { Status = AllocationStatus.Released, Note = note ?? Note }
        : throw new DomainRuleException($"That allocation is {Status} already.");

    /// <summary>Keeps only what the lot can still give.</summary>
    public LotAllocation TrimTo(decimal quantity) => this with { Quantity = Math.Max(0m, Math.Min(Quantity, quantity)) };
}

/// <summary>A lot that could feed a job's component, with what is already set aside from it (D-064).</summary>
public sealed record AllocationCandidate(
    Guid LotId,
    string Label,
    string Status,
    decimal Quantity,
    decimal AllocatedToOthers,
    decimal AllocatedToJob,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset ReceivedAt)
{
    /// <summary>What another allocation for this job could still take.</summary>
    public decimal Free => Math.Max(0m, Quantity - AllocatedToOthers - AllocatedToJob);
}

/// <summary>A suggested lot and quantity, and why it comes where it does.</summary>
public sealed record LotPick(AllocationCandidate Lot, decimal Quantity, string Reason);

/// <summary>A lot of the material that wasn't suggested, and why.</summary>
public sealed record LotExclusion(AllocationCandidate Lot, string Reason);

/// <summary>
/// Which lots to use first for a job's component (D-064): first expiry, first out, then the oldest receipt, then the label. Only an
/// available lot that won't have expired when the job needs it, with stock not set aside for other jobs, is suggested; every lot left
/// out says why. Pure; it suggests, and never decides what is consumed.
/// </summary>
public static class LotRecommendation
{
    public static (IReadOnlyList<LotPick> Picks, IReadOnlyList<LotExclusion> Excluded) Recommend(
        decimal need, IEnumerable<AllocationCandidate> candidates, DateTimeOffset neededBy, string unitCode)
    {
        var picks = new List<LotPick>();
        var excluded = new List<LotExclusion>();
        var ordered = candidates
            .OrderBy(c => c.ExpiresAt is null)
            .ThenBy(c => c.ExpiresAt)
            .ThenBy(c => c.ReceivedAt)
            .ThenBy(c => c.Label, StringComparer.Ordinal)
            .ToList();
        var left = need;
        var firstExpiry = ordered.Where(c => Problem(c, neededBy) is null && c.Free > 0).Select(c => c.ExpiresAt).FirstOrDefault(e => e is not null);
        foreach (var lot in ordered)
        {
            if (Problem(lot, neededBy) is { } problem)
            {
                excluded.Add(new LotExclusion(lot, problem));
                continue;
            }

            if (lot.Free <= 0)
            {
                excluded.Add(new LotExclusion(lot, lot.AllocatedToJob > 0 ? "Already allocated to this job." : "All of it is allocated to other jobs."));
                continue;
            }

            if (left <= 0)
            {
                excluded.Add(new LotExclusion(lot, "Not needed: the lots before it cover the need."));
                continue;
            }

            var take = Math.Min(left, lot.Free);
            left -= take;
            picks.Add(new LotPick(lot, take, Why(lot, firstExpiry, unitCode)));
        }

        return (picks, excluded);
    }

    /// <summary>Why a lot can't feed the job at all; null when it can. An allocation that has one is no longer good.</summary>
    public static string? Problem(AllocationCandidate lot, DateTimeOffset neededBy) =>
        lot.Status != LotStatus.Available ? $"It is {lot.Status.Replace('_', ' ')}; only available stock is allocated."
        : lot.ExpiresAt is { } expires && expires <= neededBy ? $"It expires {Day(expires)}, before the job needs it ({Day(neededBy)})."
        : lot.Quantity <= 0 ? "It is empty."
        : null;

    /// <summary>Why an existing allocation is no longer good; null while it is.</summary>
    public static string? AllocationProblem(LotAllocation allocation, AllocationCandidate lot, DateTimeOffset neededBy, string unitCode) =>
        Problem(lot, neededBy)
        ?? (lot.Quantity - lot.AllocatedToOthers < allocation.Quantity
            ? $"The lot has only {Qty(Math.Max(0m, lot.Quantity - lot.AllocatedToOthers))} {unitCode} left for this job, not the {Qty(allocation.Quantity)} {unitCode} allocated."
            : null);

    private static string Why(AllocationCandidate lot, DateTimeOffset? firstExpiry, string unitCode)
    {
        var order = lot.ExpiresAt is { } expires
            ? $"Expires {Day(expires)}{(expires == firstExpiry ? ", the earliest" : "")}"
            : "No expiry";
        return $"{order}; received {Day(lot.ReceivedAt)}; {Qty(lot.Free)} {unitCode} free.";
    }

    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Qty(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
