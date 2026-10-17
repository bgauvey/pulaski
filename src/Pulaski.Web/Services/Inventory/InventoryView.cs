using System.Globalization;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Web.Services.Inventory;

/// <summary>Materials of one class, for the stock list (D-027).</summary>
public sealed record StockGroup(string Key, string Label, IReadOnlyList<MaterialStockDto> Items)
{
    /// <summary>How many of the class's materials have stock on hand.</summary>
    public int InStock => Items.Count(i => i.LotCount > 0);

    public int Lots => Items.Sum(i => i.LotCount);
}

/// <summary>A lot in a trace, with how far to indent it under the lot it was reached from.</summary>
public sealed record TraceRow(LotTraceNodeDto Node, int Indent);

/// <summary>Grouping, labels and formatting for the inventory screens; no I/O.</summary>
public static class InventoryView
{
    /// <summary>The key of the group for materials with no class.</summary>
    public const string NoClass = "";

    /// <summary>Lot statuses, on hand first, for the lot search filter.</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["available", "on_hold", "quarantine", "consumed", "scrapped"];

    /// <summary>
    /// Materials grouped by class, classes by name with "No class" last, materials by code. The
    /// API already sorts this way; grouping here keeps that order whatever arrives.
    /// </summary>
    public static IReadOnlyList<StockGroup> Group(IEnumerable<MaterialStockDto> stock) =>
        [.. stock
            .GroupBy(s => s.MaterialClassCode ?? NoClass)
            .Select(g => new StockGroup(
                g.Key,
                g.Key == NoClass ? "No class" : g.First().MaterialClassName ?? g.Key,
                [.. g.OrderBy(s => s.Code, StringComparer.Ordinal)]))
            .OrderBy(g => g.Key == NoClass)
            .ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// A trace as a tree: each lot under the lot it was reached from, siblings by material then lot
    /// number. Lots whose parent isn't in the list (it shouldn't happen) are listed at the end.
    /// </summary>
    public static IReadOnlyList<TraceRow> Tree(IReadOnlyList<LotTraceNodeDto> nodes)
    {
        var root = nodes.FirstOrDefault(n => n.Depth == 0);
        if (root is null)
        {
            return [];
        }

        var children = nodes.Where(n => n.ViaLotId is not null)
            .GroupBy(n => n.ViaLotId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(n => n.MaterialCode, StringComparer.Ordinal).ThenBy(n => n.LotNumber, StringComparer.Ordinal).ToList());

        var rows = new List<TraceRow>();
        var placed = new HashSet<Guid>();
        Visit(root, 0);
        rows.AddRange(nodes.Where(n => !placed.Contains(n.LotId)).Select(n => new TraceRow(n, n.Depth)));
        return rows;

        void Visit(LotTraceNodeDto node, int indent)
        {
            if (!placed.Add(node.LotId))
            {
                return;
            }

            rows.Add(new TraceRow(node, indent));
            foreach (var child in children.GetValueOrDefault(node.LotId, []))
            {
                Visit(child, indent + 1);
            }
        }
    }

    /// <summary>A quantity with up to six decimals and thousands separators: 1,234.5.</summary>
    public static string Qty(decimal value) => value.ToString("#,0.######", CultureInfo.InvariantCulture);

    /// <summary>A signed change: +5, −12.5.</summary>
    public static string Delta(decimal value) => (value > 0 ? "+" : value < 0 ? "−" : "") + Qty(Math.Abs(value));

    /// <summary>How a lot is named on screen: its number, or its location's balance for a material that isn't lot-tracked.</summary>
    public static string LotLabel(string lotNumber, bool isBulk, string? locationCode) =>
        isBulk ? $"Balance at {locationCode ?? lotNumber.TrimStart('@')}" : lotNumber;

    /// <summary>What a movement was, in words.</summary>
    public static string EventLabel(string kind) => kind switch
    {
        "received" => "Received",
        "produced" => "Produced",
        "adjusted" => "Written up",
        "scrapped" => "Written off",
        "consumed" => "Consumed",
        "moved" => "Moved",
        "status_changed" => "Status changed",
        _ => Display.Label(kind),
    };

    /// <summary>Chip classes for a lot status: on hold and quarantine warn, used-up lots are muted.</summary>
    public static string StatusChip(string status) => status switch
    {
        "available" => "chip chip-strong",
        "on_hold" or "quarantine" => "chip chip-warning",
        "consumed" or "scrapped" => "chip chip-muted",
        _ => "chip",
    };

    /// <summary>True when an expiry falls within <paramref name="days"/> of <paramref name="now"/> (or has passed).</summary>
    public static bool ExpiresSoon(DateTimeOffset? expiresAt, DateTimeOffset now, int days = 14) =>
        expiresAt is { } at && at <= now.AddDays(days);
}
