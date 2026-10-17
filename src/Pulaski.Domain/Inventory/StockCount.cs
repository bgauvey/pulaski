using System.Globalization;

namespace Pulaski.Domain.Inventory;

/// <summary>Stock count lifecycle (D-030). Values match <c>core.stock_count.status</c>.</summary>
public static class StockCountStatus
{
    public const string Open = "open";
    public const string Posted = "posted";
    public const string Cancelled = "cancelled";
}

/// <summary>A count of one location (D-030): open while people count, then posted or cancelled.</summary>
public sealed record StockCount(Guid Id, Guid SiteId, string Number, Guid LocationId, string Status, string? Note)
{
    /// <summary>The count's number: <c>CNT-20261016-3</c> for the site's third count started that day (UTC).</summary>
    public static string NumberFor(DateTimeOffset day, int sequence) =>
        $"CNT-{day.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{sequence.ToString(CultureInfo.InvariantCulture)}";

    /// <exception cref="DomainRuleException">The count is posted or cancelled.</exception>
    public void EnsureOpen()
    {
        if (Status != StockCountStatus.Open)
        {
            throw new DomainRuleException($"Count {Number} is {Status}; it can't be changed.");
        }
    }

    public StockCount Post()
    {
        EnsureOpen();
        return this with { Status = StockCountStatus.Posted };
    }

    public StockCount Cancel()
    {
        EnsureOpen();
        return this with { Status = StockCountStatus.Cancelled };
    }
}

/// <summary>One lot in a count: what the snapshot expected, and what was counted, when and by whom.</summary>
public sealed record StockCountLine(Guid Id, Guid CountId, Guid LotId, decimal ExpectedQty, decimal? CountedQty, DateTimeOffset? CountedAt, string? CountedBy);

/// <summary>A counted lot as it is now: the lot and when it last changed.</summary>
public sealed record CountedLot(Lot Lot, DateTimeOffset UpdatedAt);

/// <summary>A lot whose count differs from its quantity now, and by how much (positive: more was counted).</summary>
public sealed record CountAdjustment(StockCountLine Line, Lot Lot, decimal Variance);

/// <summary>
/// What posting a count would do (D-030): a write-up or write-off for each counted lot whose count
/// differs from its quantity now. Refused, with every reason, while a line is uncounted or a lot
/// changed after its line was counted.
/// </summary>
public static class StockCountPosting
{
    public static (IReadOnlyList<CountAdjustment> Adjustments, IReadOnlyList<string> Problems) Plan(
        IEnumerable<StockCountLine> lines, IReadOnlyDictionary<Guid, CountedLot> lots)
    {
        List<CountAdjustment> adjustments = [];
        List<string> problems = [];
        foreach (var line in lines)
        {
            var current = lots[line.LotId];
            if (line.CountedQty is not { } counted)
            {
                problems.Add($"{current.Lot.Name} isn't counted yet; enter 0 if it isn't there.");
            }
            else if (current.UpdatedAt > line.CountedAt)
            {
                problems.Add($"{current.Lot.Name} changed after it was counted; count it again.");
            }
            else if (counted != current.Lot.Quantity)
            {
                adjustments.Add(new CountAdjustment(line, current.Lot, counted - current.Lot.Quantity));
            }
        }

        return (adjustments, problems);
    }
}
