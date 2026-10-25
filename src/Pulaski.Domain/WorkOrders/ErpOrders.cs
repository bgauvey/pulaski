using Pulaski.Domain.Materials;

namespace Pulaski.Domain.WorkOrders;

/// <summary>
/// What to do with an ERP order whose quantity isn't whole batches (D-066). Values match <c>erp_batch_rule</c> on the site's scheduling
/// policy (the default) and on the material (an override).
/// </summary>
public static class ErpBatchRule
{
    /// <summary>The import refuses the order; the ERP must send whole batches.</summary>
    public const string Reject = "reject";

    /// <summary>Plan the fewest whole batches that hold the ERP's quantity.</summary>
    public const string RoundUp = "round_up";

    /// <summary>Plan the most whole batches the ERP's quantity holds (at least one batch, else refused).</summary>
    public const string RoundDown = "round_down";

    /// <summary>Import it at the ERP's quantity, held: it can't be dispatched until a planner sets a whole-batch quantity.</summary>
    public const string Hold = "hold";

    public static IReadOnlyList<string> All { get; } = [Reject, RoundUp, RoundDown, Hold];

    /// <summary>The rule in force: the material's own, else the site's, else reject.</summary>
    public static string For(string? material, string? site) => material ?? site ?? Reject;

    /// <summary>
    /// The quantity to plan for the ERP's <paramref name="quantity"/> (in the base unit) under <paramref name="rule"/>, and how it was changed.
    /// A material made continuously, or a quantity already whole batches, is planned as sent.
    /// </summary>
    /// <param name="quantity">The ERP's quantity in the material's base unit.</param>
    /// <param name="batching">The material's batching; null when it is made continuously.</param>
    /// <param name="rule">One of <see cref="All"/>.</param>
    /// <param name="what">The order, for messages.</param>
    /// <param name="unitCode">The base unit's code, for messages.</param>
    /// <exception cref="DomainRuleException">The rule refuses it, or rounding down leaves nothing.</exception>
    public static ErpQuantity Apply(decimal quantity, Batching? batching, string rule, string what, string unitCode)
    {
        if (batching is null || batching.WholeBatches(quantity) is not null)
        {
            return new ErpQuantity(quantity, null, null);
        }

        var batches = quantity / batching.Size;
        var sent = $"{Batching.Qty(quantity)} {unitCode} is {Batching.Qty(batches)} batches of {Batching.Qty(batching.Size)} {unitCode}";
        switch (rule)
        {
            case RoundUp:
                var up = Math.Ceiling(batches) * batching.Size;
                return new ErpQuantity(up, BatchAdjustment.RoundedUp, $"{sent}: planned {Batching.Qty(up)} {unitCode}, rounded up to whole batches.");
            case RoundDown:
                var down = Math.Floor(batches) * batching.Size;
                return down > 0
                    ? new ErpQuantity(down, BatchAdjustment.RoundedDown, $"{sent}: planned {Batching.Qty(down)} {unitCode}, rounded down to whole batches.")
                    : throw new DomainRuleException($"{what}: {sent}; rounded down it would be nothing. Send at least {Batching.Qty(batching.Size)} {unitCode}.");
            case Hold:
                return new ErpQuantity(quantity, BatchAdjustment.Held, $"{sent}: held until a planner sets a whole-batch quantity.");
            default:
                throw new DomainRuleException(
                    $"{what}: {sent}, and this site takes only whole batches from the ERP. Send {Batching.Qty(Math.Max(1, Math.Floor(batches)) * batching.Size)} or {Batching.Qty(Math.Ceiling(batches) * batching.Size)} {unitCode}.");
        }
    }
}

/// <summary>Values match <c>core.work_order.batch_adjustment</c>.</summary>
public static class BatchAdjustment
{
    public const string RoundedUp = "rounded_up";
    public const string RoundedDown = "rounded_down";
    public const string Held = "held";
}

/// <summary>The quantity to plan, in the base unit, and how it differs from what the ERP sent; null adjustment when it doesn't.</summary>
public sealed record ErpQuantity(decimal Quantity, string? Adjustment, string? Note);

/// <summary>
/// An order as the ERP sends it (D-066), after codes are resolved: the fields the ERP owns (product, quantity, due date) plus its start and
/// priority. Kept on the order as its pending change when it can't be applied at once.
/// </summary>
public sealed record ErpOrderChange(
    decimal Quantity,
    Guid UomId,
    DateTimeOffset PlannedStart,
    DateTimeOffset? DueAt,
    int Priority,
    string? Version,
    bool Cancelled,
    DateTimeOffset ReceivedAt);
