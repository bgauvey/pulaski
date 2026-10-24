using System.Globalization;

namespace Pulaski.Web.Services;

/// <summary>Formatting helpers for codes and times.</summary>
public static class Display
{
    /// <summary>Work order statuses in lifecycle order; mirrors Pulaski.Domain.WorkOrders.WorkOrderStatus.</summary>
    public static IReadOnlyList<string> WorkOrderStatuses { get; } =
        ["planned", "dispatched", "firmed", "in_progress", "completed", "closed", "cancelled"];

    /// <summary>Button text for moving a work order from one status to another.</summary>
    public static string StatusAction(string from, string to) => (from, to) switch
    {
        (_, "dispatched") => "Dispatch",
        ("dispatched", "planned") => "Un-dispatch",
        (_, "firmed") => "Firm",
        (_, "in_progress") when from == "completed" => "Reopen",
        (_, "in_progress") => "Start",
        (_, "completed") => "Complete",
        (_, "closed") => "Close",
        (_, "cancelled") => "Cancel",
        _ => Label(to),
    };

    /// <summary>A reason as pickers list it: under its groups, "Mechanical › Jam" (D-062).</summary>
    public static string ReasonLabel(Pulaski.Contracts.Inventory.ReasonCodeDto reason) =>
        reason.GroupPath is { } path ? $"{path} › {reason.Name}" : reason.Name;

    /// <summary>'in_progress' → 'In progress'.</summary>
    public static string Label(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "";
        }

        var text = code.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>A UTC instant shown in the site's time zone (falls back to UTC).</summary>
    public static string SiteTime(DateTimeOffset? value, string? timeZone)
    {
        if (value is null)
        {
            return "";
        }

        var local = ToSite(value.Value, timeZone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset ToSite(DateTimeOffset value, string? timeZone) =>
        TryFindZone(timeZone) is { } zone ? TimeZoneInfo.ConvertTime(value, zone) : value.ToUniversalTime();

    /// <summary>A wall-clock time entered for a site, as an instant.</summary>
    public static DateTimeOffset? FromSiteLocal(DateTime? local, string? timeZone)
    {
        if (local is null)
        {
            return null;
        }

        var unspecified = DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified);
        var zone = TryFindZone(timeZone) ?? TimeZoneInfo.Utc;
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>
    /// Chip classes for a work order status. Work order status is not machine state, so it stays
    /// neutral: only in-progress orders get the running dot.
    /// </summary>
    public static string StatusChip(string status) => status switch
    {
        "dispatched" or "firmed" => "chip chip-strong",
        "in_progress" => "chip chip-strong chip-live",
        "closed" or "cancelled" => "chip chip-muted",
        _ => "chip",
    };

    /// <summary>Chip classes for a job status: neutral like work orders, with the running dot for a running job.</summary>
    public static string JobStatusChip(string status) => status switch
    {
        "running" => "chip chip-strong chip-live",
        "dispatched" or "firmed" => "chip chip-strong",
        "paused" => "chip chip-strong",
        _ => "chip",
    };

    private static TimeZoneInfo? TryFindZone(string? timeZone) =>
        timeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone) ? zone : null;
}
