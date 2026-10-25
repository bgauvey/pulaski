using System.Globalization;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services;

/// <summary>How the work order screen words an ERP order (D-066).</summary>
public static class WorkOrderView
{
    /// <summary>What Pulaski did with the ERP's quantity.</summary>
    public static string Adjustment(string? adjustment) => adjustment switch
    {
        "rounded_up" => "rounded up to whole batches.",
        "rounded_down" => "rounded down to whole batches.",
        "held" => "held until a planner sets whole batches.",
        _ => "planned as sent.",
    };

    /// <summary>What the pending change would change, field by field: "Quantity 1,100 → 1,200 m; due 12 Nov → 14 Nov".</summary>
    public static string PendingSummary(WorkOrderDto order, ErpPendingChangeDto change, string? timeZone)
    {
        if (change.Cancelled)
        {
            return "Cancelled in the ERP. Accepting cancels the order and its jobs, if none has started.";
        }

        var parts = new List<string>();
        var current = order.ErpQty ?? order.PlannedQty;
        if (change.Quantity != current)
        {
            parts.Add($"quantity {Qty(current)} → {Qty(change.Quantity)} {change.UomCode}");
        }

        if (change.PlannedStart != order.PlannedStart)
        {
            parts.Add($"start {Display.SiteTime(order.PlannedStart, timeZone)} → {Display.SiteTime(change.PlannedStart, timeZone)}");
        }

        if (change.DueAt != order.DueAt)
        {
            parts.Add($"due {(order.DueAt is null ? "none" : Display.SiteTime(order.DueAt, timeZone))} → {(change.DueAt is null ? "none" : Display.SiteTime(change.DueAt, timeZone))}");
        }

        if (change.Priority != order.Priority)
        {
            parts.Add($"priority {order.Priority} → {change.Priority}");
        }

        var summary = parts.Count == 0 ? "Nothing Pulaski plans with changes." : char.ToUpperInvariant(parts[0][0]) + string.Join("; ", parts)[1..] + ".";
        return summary + " Accepting re-plans its jobs while none has started.";
    }

    private static string Qty(decimal value) => value.ToString("#,0.######", CultureInfo.InvariantCulture);
}
