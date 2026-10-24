using System.Globalization;
using Pulaski.Application.Errors;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// Back-dated quantity movements (D-057): a receipt, consumption, production, write-up or write-off can count from an earlier time
/// than it is recorded. The record time is always kept; stock at a time sums movements by when they count, so it is the same
/// whatever order they were entered in. The API lets only holders of <c>inventory.backdate</c> send an effective time.
/// </summary>
public static class Backdating
{
    public const int MaxDays = 31;

    /// <summary>
    /// When the movement counts: <paramref name="now"/> when no time is given, else the time given, which may not be ahead of now or
    /// more than <see cref="MaxDays"/> days back, and needs a comment saying why.
    /// </summary>
    /// <exception cref="ValidationException">The time is in the future or too far back, or the comment is missing.</exception>
    public static DateTimeOffset EffectiveAt(DateTimeOffset? effectiveAt, DateTimeOffset now, string? comment)
    {
        if (effectiveAt is not { } at || at >= now)
        {
            new Validator().Require(effectiveAt is null || effectiveAt <= now.AddMinutes(1), "EffectiveAt", "Can't be in the future.").ThrowIfInvalid();
            return now;
        }

        new Validator()
            .Require(at >= now.AddDays(-MaxDays), "EffectiveAt", $"At most {MaxDays} days back.")
            .Require(!string.IsNullOrWhiteSpace(comment), "Comment", "Say why the movement is back-dated.")
            .ThrowIfInvalid();
        return at;
    }

    /// <summary>
    /// A movement of <paramref name="delta"/> counting at <paramref name="effectiveAt"/> fits the lot's history: not before the lot
    /// had any stock movement, not before a posted stock count that counted it, and never taking it below zero at any time.
    /// Nothing to check when it counts now.
    /// </summary>
    /// <exception cref="DomainRuleException">It doesn't fit.</exception>
    public static async Task EnsureFitsAsync(
        IInventoryRepository inventory, Lot lot, decimal delta, DateTimeOffset effectiveAt, DateTimeOffset now, string unitCode, CancellationToken cancellationToken)
    {
        if (effectiveAt >= now)
        {
            return;
        }

        var timeline = await inventory.ListQuantityTimelineAsync(lot.Id, cancellationToken);
        // A balance (D-031) is only a container: stock arriving in it may count from before its first movement.
        if (timeline.Count > 0 && effectiveAt < timeline[0].EffectiveAt && !(lot.IsBulk && delta > 0))
        {
            throw new DomainRuleException($"{lot.Name} had no stock until {Time(timeline[0].EffectiveAt)}; a movement can't count before that.");
        }

        if (await inventory.LatestCountOfLotAsync(lot.Id, cancellationToken) is { } counted && effectiveAt < counted)
        {
            throw new DomainRuleException($"{lot.Name} was counted, and the count posted, at {Time(counted)}; a movement can't count before that.");
        }

        // Replay the lot in the order its movements count, with this one in its place.
        var balance = 0m;
        var placed = false;
        foreach (var point in timeline)
        {
            if (!placed && point.EffectiveAt > effectiveAt)
            {
                balance += delta;
                placed = true;
                Check(balance, effectiveAt);
            }

            balance += point.Delta;
            Check(balance, point.EffectiveAt);
        }

        if (!placed)
        {
            Check(balance + delta, effectiveAt);
        }

        void Check(decimal value, DateTimeOffset at)
        {
            if (value < 0)
            {
                throw new DomainRuleException(
                    $"Counting from {Time(effectiveAt)}, {lot.Name} would have been {Qty(-value)} {unitCode} short at {Time(at)}; it can't go below zero at any time.");
            }
        }
    }

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Qty(decimal value) => value.ToString("#,0.######", CultureInfo.InvariantCulture);
}
