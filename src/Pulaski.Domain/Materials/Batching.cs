using System.Globalization;

namespace Pulaski.Domain.Materials;

/// <summary>Values match <c>core.material.batch_rule</c> (D-036).</summary>
public static class BatchRule
{
    /// <summary>Every batch is exactly the batch size; a partial batch waits for approval. The default for finished goods.</summary>
    public const string Exact = "exact";

    /// <summary>A batch may differ from the batch size by the tolerance; a partial batch is accepted. The default for everything else.</summary>
    public const string Near = "near";

    public static IReadOnlyList<string> All { get; } = [Exact, Near];

    /// <summary>The rule a material starts with when it gets a batch size: finished goods can't be sold as partials.</summary>
    public static string DefaultFor(string materialType) => materialType == "finished" ? Exact : Near;
}

/// <summary>Values match <c>core.partial_batch.status</c> (D-036).</summary>
public static class PartialBatchStatus
{
    public const string Accepted = "accepted";
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    /// <summary>Its report was reversed (D-063): it no longer counts as the run's partial batch.</summary>
    public const string Reversed = "reversed";
}

/// <summary>
/// How a material is made in batches (D-036): the batch size in its base unit, the rule, and for the
/// near rule how far a batch may be from the size (49.3 for 50 with 2%). Orders and jobs are whole
/// batches; a report is whole batches; a partial batch is an exception, reported only when a line stops.
/// </summary>
public sealed record Batching(decimal Size, decimal TolerancePct, string Rule)
{
    /// <summary>Quantities closer than this to a whole number of batches count as whole (decimal places of <c>core.qty</c>).</summary>
    private const decimal Epsilon = 0.000001m;

    /// <exception cref="DomainRuleException">The size, tolerance or rule is invalid.</exception>
    public static Batching Create(decimal size, decimal tolerancePct, string rule)
    {
        if (size <= 0)
        {
            throw new DomainRuleException("The batch size must be greater than zero.");
        }

        if (tolerancePct is < 0 or > 50)
        {
            throw new DomainRuleException("The batch tolerance must be between 0 and 50 percent.");
        }

        if (!BatchRule.All.Contains(rule))
        {
            throw new DomainRuleException($"'{rule}' isn't a batch rule; use exact or near.");
        }

        return new Batching(size, tolerancePct, rule);
    }

    public bool IsExact => Rule == BatchRule.Exact;

    /// <summary>The smallest a batch may be: the size for the exact rule, the size less the tolerance for the near one.</summary>
    public decimal Low => IsExact ? Size : Size * (1 - TolerancePct / 100m);

    public decimal High => IsExact ? Size : Size * (1 + TolerancePct / 100m);

    /// <summary>How many batches <paramref name="quantity"/> (in the base unit) is, when it is a whole number of them.</summary>
    public int? WholeBatches(decimal quantity)
    {
        var batches = quantity / Size;
        var whole = Math.Round(batches, MidpointRounding.AwayFromZero);
        return whole > 0 && Math.Abs(batches - whole) * Size <= Epsilon ? (int)whole : null;
    }

    /// <summary>Refuses a planned quantity that isn't a whole number of batches, naming the two nearest that are.</summary>
    /// <param name="quantity">In the base unit.</param>
    /// <param name="what">What is being planned, for the message, such as "Work order WO-1".</param>
    /// <param name="unitCode">The base unit's code, for the message.</param>
    /// <exception cref="DomainRuleException">It would need a partial batch.</exception>
    public void EnsureWholeBatches(decimal quantity, string what, string unitCode)
    {
        if (WholeBatches(quantity) is not null)
        {
            return;
        }

        var below = Math.Floor(quantity / Size);
        var above = below + 1;
        throw new DomainRuleException(
            $"{what}: {Qty(quantity)} {unitCode} is {Qty(quantity / Size)} batches of {Qty(Size)} {unitCode}, and only whole batches are planned. " +
            (below > 0 ? $"Plan {Qty(below * Size)} or {Qty(above * Size)} {unitCode}." : $"Plan at least {Qty(Size)} {unitCode}."));
    }

    /// <summary>Whether one reported batch weighs what a batch should.</summary>
    public bool IsBatch(decimal quantity) => quantity >= Low - Epsilon && quantity <= High + Epsilon;

    /// <exception cref="DomainRuleException">The quantity isn't a batch.</exception>
    public void EnsureBatch(decimal quantity, string unitCode)
    {
        if (!IsBatch(quantity))
        {
            throw new DomainRuleException(IsExact
                ? $"{Qty(quantity)} {unitCode} isn't a batch: every batch is exactly {Qty(Size)} {unitCode}. A short batch is reported as a partial batch when the line stops."
                : $"{Qty(quantity)} {unitCode} isn't a batch: a batch is {Qty(Low)} to {Qty(High)} {unitCode} ({Qty(Size)} ± {Qty(TolerancePct)}%). " +
                  "A short batch is reported as a partial batch when the line stops.");
        }
    }

    /// <summary>A partial batch is less than a batch: above zero and below <see cref="Low"/>.</summary>
    /// <exception cref="DomainRuleException">It is nothing, or a whole batch.</exception>
    public void EnsurePartial(decimal quantity, string unitCode)
    {
        if (quantity <= 0 || quantity >= Low - Epsilon)
        {
            throw new DomainRuleException(
                $"A partial batch is less than a batch: more than 0 and less than {Qty(Low)} {unitCode}. Report a whole batch as a batch.");
        }
    }

    /// <summary>The order's batches shared between equipment by percentage, whole batches each, largest remainders first; every share gets at least one.</summary>
    /// <exception cref="DomainRuleException">There are fewer batches than shares.</exception>
    public static IReadOnlyList<int> Share(int batches, IReadOnlyList<decimal> sharePercents)
    {
        if (batches < sharePercents.Count)
        {
            throw new DomainRuleException($"{batches} batches can't be shared between {sharePercents.Count} lines; plan at least one batch for each.");
        }

        var exact = sharePercents.Select(p => batches * p / 100m).ToList();
        var counts = exact.Select(e => Math.Max(1, (int)Math.Floor(e))).ToList();
        var order = exact.Select((e, i) => (Remainder: e - Math.Floor(e), Index: i)).OrderByDescending(x => x.Remainder).ThenBy(x => x.Index).ToList();
        for (var k = 0; counts.Sum() < batches; k = (k + 1) % order.Count)
        {
            counts[order[k].Index]++;
        }

        for (var k = order.Count - 1; counts.Sum() > batches; k = k == 0 ? order.Count - 1 : k - 1)
        {
            if (counts[order[k].Index] > 1)
            {
                counts[order[k].Index]--;
            }
        }

        return counts;
    }

    /// <summary>
    /// The batching on one line (D-065): the line's own batch size and tolerance from its capability where it has them (#85), else the
    /// material's. The rule is always the material's: it describes the product, not the equipment.
    /// </summary>
    public Batching OnLine(decimal? lineSize, decimal? lineTolerancePct = null) => this with
    {
        Size = lineSize is > 0 ? lineSize.Value : Size,
        TolerancePct = lineTolerancePct is >= 0 and <= 50 ? lineTolerancePct.Value : TolerancePct,
    };

    /// <summary>
    /// Shares <paramref name="quantity"/> between equipment whose batch sizes may differ (D-065): each gets whole batches of its own
    /// size, near its percentage and at least one, adding up to the quantity exactly. With one size for all it is <see cref="Share"/>.
    /// </summary>
    /// <param name="quantity">What every share together makes, in the unit the sizes are in.</param>
    /// <param name="shares">Each equipment's percentage and batch size, in order.</param>
    /// <param name="what">What is being planned, for the message.</param>
    /// <param name="unitCode">The unit's code, for the message.</param>
    /// <returns>Each share's number of batches.</returns>
    /// <exception cref="DomainRuleException">No whole batches add up to the quantity; the message names the nearest quantities that do.</exception>
    public static IReadOnlyList<int> ShareBySize(decimal quantity, IReadOnlyList<(decimal Percent, decimal Size)> shares, string what, string unitCode)
    {
        if (shares.Select(s => s.Size).Distinct().Count() == 1)
        {
            var size = shares[0].Size;
            var batches = quantity / size;
            var whole = Math.Round(batches, MidpointRounding.AwayFromZero);
            if (whole <= 0 || Math.Abs(batches - whole) * size > Epsilon)
            {
                var under = Math.Max(Math.Floor(batches), shares.Count);
                throw new DomainRuleException(
                    $"{what}: {Qty(quantity)} {unitCode} isn't whole batches of {Qty(size)} {unitCode}. Plan {Qty(under * size)} or {Qty((under + 1) * size)} {unitCode}.");
            }

            return Share((int)whole, [.. shares.Select(s => s.Percent)]);
        }

        // Every combination near each share's target, a couple of batches either way: the one that adds up and stays closest to the shares.
        var targets = shares.Select(s => quantity * s.Percent / 100m / s.Size).ToList();
        var ranges = targets.Select(t => (From: Math.Max(1, (int)Math.Floor(t) - 2), To: Math.Max(1, (int)Math.Ceiling(t) + 2))).ToList();
        int[]? best = null;
        var bestDistance = decimal.MaxValue;
        decimal? below = null, above = null;
        var counts = new int[shares.Count];
        void Search(int i, decimal total)
        {
            if (i == shares.Count)
            {
                if (Math.Abs(total - quantity) <= Epsilon)
                {
                    var distance = counts.Select((c, k) => Math.Abs(c - targets[k]) * shares[k].Size).Sum();
                    if (distance < bestDistance)
                    {
                        (best, bestDistance) = ([.. counts], distance);
                    }
                }
                else if (total < quantity)
                {
                    below = below is { } b && b >= total ? b : total;
                }
                else
                {
                    above = above is { } a && a <= total ? a : total;
                }

                return;
            }

            for (var c = ranges[i].From; c <= ranges[i].To; c++)
            {
                counts[i] = c;
                Search(i + 1, total + c * shares[i].Size);
            }
        }

        Search(0, 0m);
        if (best is not null)
        {
            return best;
        }

        var sizes = string.Join(", ", shares.Select(s => $"{Qty(s.Size)}"));
        var nearest = string.Join(" or ", new[] { below, above }.Where(q => q is not null).Select(q => Qty(q!.Value)));
        throw new DomainRuleException(
            $"{what}: {Qty(quantity)} {unitCode} can't be shared in whole batches of each line's size ({sizes} {unitCode}). " +
            $"Plan {nearest} {unitCode}, or change the shares.");
    }

    /// <summary>The fewest whole batches that hold <paramref name="quantity"/>.</summary>
    public decimal RoundUp(decimal quantity) => quantity <= 0 ? Size : Math.Ceiling(quantity / Size - Epsilon) * Size;

    /// <summary>Whole batches at most <paramref name="quantity"/> (#85); 0 when it is less than a batch.</summary>
    public decimal RoundDown(decimal quantity) => quantity <= 0 ? 0 : Math.Floor(quantity / Size + Epsilon) * Size;

    public static string Qty(decimal value) => Math.Round(value, 3).ToString("#,0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// A batch cut short when a line stopped (D-036), in a lot of its own. Under the exact rule it waits on
/// hold for someone to approve it (the lot is released) or reject it (the lot is scrapped); under the
/// near rule it is accepted as it is, since it will be used.
/// </summary>
public sealed record PartialBatch(
    Guid Id,
    Guid JobId,
    Guid RunId,
    Guid LotId,
    decimal Quantity,
    Guid UomId,
    decimal BatchSize,
    string BatchRule,
    string Status,
    DateTimeOffset OccurredAt,
    string? ReportedBy,
    DateTimeOffset? DecidedAt = null,
    string? DecidedBy = null,
    string? DecisionComment = null,
    Guid? ScrapReasonCodeId = null)
{
    public static PartialBatch Report(
        Guid id, Guid jobId, Guid runId, Guid lotId, decimal quantity, Guid uomId, Batching batching, DateTimeOffset occurredAt, string? reportedBy) =>
        new(id, jobId, runId, lotId, quantity, uomId, batching.Size, batching.Rule,
            batching.IsExact ? PartialBatchStatus.Pending : PartialBatchStatus.Accepted, occurredAt, reportedBy);

    /// <exception cref="DomainRuleException">It isn't waiting for approval.</exception>
    public PartialBatch Approve(DateTimeOffset at, string by, string? comment)
    {
        EnsurePending();
        return this with { Status = PartialBatchStatus.Approved, DecidedAt = at, DecidedBy = by, DecisionComment = comment };
    }

    /// <exception cref="DomainRuleException">It isn't waiting for approval.</exception>
    public PartialBatch Reject(DateTimeOffset at, string by, Guid scrapReasonCodeId, string? comment)
    {
        EnsurePending();
        return this with
        {
            Status = PartialBatchStatus.Rejected,
            DecidedAt = at,
            DecidedBy = by,
            DecisionComment = comment,
            ScrapReasonCodeId = scrapReasonCodeId,
        };
    }

    /// <summary>Its report was reversed (D-063). A rejected one was written off, so its report can't be until that is reversed.</summary>
    /// <exception cref="DomainRuleException">It was rejected or reversed already.</exception>
    public PartialBatch Reverse() => Status is PartialBatchStatus.Rejected or PartialBatchStatus.Reversed
        ? throw new DomainRuleException($"This partial batch was {Status}; its report can't be reversed while it is.")
        : this with { Status = PartialBatchStatus.Reversed };

    private void EnsurePending()
    {
        if (Status != PartialBatchStatus.Pending)
        {
            throw new DomainRuleException(Status == PartialBatchStatus.Accepted
                ? "This partial batch didn't need approval; it was accepted when reported."
                : $"This partial batch was already {Status}.");
        }
    }
}
