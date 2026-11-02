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

/// <summary>
/// How a site orders lots expiring the same day (#82), in the order it lists them, before the oldest receipt. Values of
/// <c>core.allocation_settings.preferences</c>.
/// </summary>
public static class AllocationPreferences
{
    /// <summary>Lots already at the job's line (its feed points and line-side stock below it).</summary>
    public const string NearLine = "near_line";

    /// <summary>Lots already packed in a container, ready to move.</summary>
    public const string InContainer = "in_container";

    /// <summary>A lot that covers the rest of the need alone, so nothing is split.</summary>
    public const string WholeLot = "whole_lot";

    /// <summary>Smaller lots first, so part lots are used up.</summary>
    public const string PartLotsFirst = "part_lots_first";

    public static IReadOnlyList<string> All { get; } = [NearLine, InContainer, WholeLot, PartLotsFirst];
}

/// <summary>Values of <c>core.lot_allocation.basis</c>: picked by a person, or taken from the recommendation.</summary>
public static class AllocationBasis
{
    public const string Chosen = "chosen";
    public const string Recommended = "recommended";
}

/// <summary>
/// Stock of one lot set aside for one of a job's components (D-064), in the lot's base unit. While active, other jobs see it as taken;
/// consuming the lot into its job uses it up. It never changes the lot: final consumption stays under the inventory rules (D-027). An
/// approved alternate's allocation (#82) stands in for <see cref="ForMaterialId"/>, <see cref="Ratio"/> of its base units per one of that.
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
    string? Note = null,
    Guid? ForMaterialId = null,
    decimal? Ratio = null)
{
    public bool IsActive => Status == AllocationStatus.Active;

    /// <summary>The planned component it covers: its own material, or the one it stands in for.</summary>
    public Guid ComponentId => ForMaterialId ?? MaterialId;

    /// <summary>How much of the component it covers, in the component's base unit.</summary>
    public decimal Covers => Ratio is { } ratio ? Math.Round(Quantity / ratio, 6, MidpointRounding.AwayFromZero) : Quantity;

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
    DateTimeOffset ReceivedAt,
    bool NearLine = false,
    bool InContainer = false)
{
    /// <summary>What another allocation for this job could still take.</summary>
    public decimal Free => Math.Max(0m, Quantity - AllocatedToOthers - AllocatedToJob);
}

/// <summary>A suggested lot and quantity, and why it comes where it does.</summary>
public sealed record LotPick(AllocationCandidate Lot, decimal Quantity, string Reason);

/// <summary>A lot of the material that wasn't suggested, and why.</summary>
public sealed record LotExclusion(AllocationCandidate Lot, string Reason);

/// <summary>
/// Which lots to use first for a job's component (D-064): first expiry, first out, by day; among lots expiring the same day (or not at all),
/// the site's <see cref="AllocationPreferences"/> in its order (#82); then the oldest receipt, then the label. Only an available lot that
/// won't have expired when the job needs it, with stock not set aside for other jobs, is suggested; every lot left out says why. Pure; it
/// suggests, and never decides what is consumed.
/// </summary>
public static class LotRecommendation
{
    public static (IReadOnlyList<LotPick> Picks, IReadOnlyList<LotExclusion> Excluded) Recommend(
        decimal need, IEnumerable<AllocationCandidate> candidates, DateTimeOffset neededBy, string unitCode, IReadOnlyList<string>? preferences = null)
    {
        var picks = new List<LotPick>();
        var excluded = new List<LotExclusion>();
        var ordered = Order(candidates, need, preferences ?? []).ToList();
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
            picks.Add(new LotPick(lot, take, Why(lot, firstExpiry, unitCode, preferences ?? [], left)));
            left -= take;
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

    /// <summary>Expiry day first; then the preferences, each ranking the lots it favours first; then the oldest receipt and the label.</summary>
    private static IOrderedEnumerable<AllocationCandidate> Order(IEnumerable<AllocationCandidate> candidates, decimal need, IReadOnlyList<string> preferences)
    {
        var ordered = candidates.OrderBy(c => c.ExpiresAt is null).ThenBy(c => c.ExpiresAt?.UtcDateTime.Date);
        foreach (var preference in preferences)
        {
            ordered = preference switch
            {
                AllocationPreferences.NearLine => ordered.ThenBy(c => !c.NearLine),
                AllocationPreferences.InContainer => ordered.ThenBy(c => !c.InContainer),
                AllocationPreferences.WholeLot => ordered.ThenBy(c => c.Free < need),
                AllocationPreferences.PartLotsFirst => ordered.ThenBy(c => c.Free),
                _ => ordered,
            };
        }

        return ordered.ThenBy(c => c.ReceivedAt).ThenBy(c => c.Label, StringComparer.Ordinal);
    }

    private static string Why(AllocationCandidate lot, DateTimeOffset? firstExpiry, string unitCode, IReadOnlyList<string> preferences, decimal left)
    {
        var order = lot.ExpiresAt is { } expires
            ? $"Expires {Day(expires)}{(expires == firstExpiry ? ", the earliest" : "")}"
            : "No expiry";
        var favoured = preferences.Select(p => p switch
        {
            AllocationPreferences.NearLine when lot.NearLine => "at the line",
            AllocationPreferences.InContainer when lot.InContainer => "in a container",
            AllocationPreferences.WholeLot when lot.Free >= left => "covers the rest alone",
            AllocationPreferences.PartLotsFirst when lot.Free < left => "a part lot, used up first",
            _ => null,
        }).OfType<string>().ToList();
        return $"{order}{(favoured.Count > 0 ? "; " + string.Join(", ", favoured) : "")}; received {Day(lot.ReceivedAt)}; {Qty(lot.Free)} {unitCode} free.";
    }

    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Qty(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
