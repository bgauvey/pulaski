using Pulaski.Contracts.Integrations;

namespace Pulaski.Web.Services.Integrations;

/// <summary>How the ERP delivery screen shows a delivery (D-061).</summary>
public static class DeliveryView
{
    /// <summary>The status as a chip: failed is danger, held or retrying a warning, delivered plain, skipped muted.</summary>
    public static (string Text, string Css) Status(DeliveryDto d) => d.Status switch
    {
        "failed" => ("Failed", "chip chip-danger"),
        "pending" when d.IsHeld => ("Held", "chip chip-warning"),
        "pending" when d.Attempts > 0 => ("Retrying", "chip chip-warning"),
        "pending" => ("Queued", "chip"),
        "delivered" => ("Delivered", "chip chip-strong"),
        "skipped" => ("Skipped", "chip chip-muted"),
        _ => (d.Status, "chip"),
    };

    /// <summary>A line on what happens next, in the site-less UTC the screen uses.</summary>
    public static string Next(DeliveryDto d, DateTimeOffset now) => d.Status switch
    {
        "failed" => "Waits for someone to retry or skip it.",
        "pending" when d.IsHeld => "Waits behind an earlier delivery for the same lot.",
        "pending" when d.NextAttemptAt is { } at && at > now => $"Next try in {Wait(at - now)}.",
        "pending" => "Goes on the next round.",
        "skipped" => d.ResolveNote is { } note ? $"Skipped by {d.ResolvedBy}: {note}" : "Skipped.",
        _ => "",
    };

    /// <summary>An event type without its prefix and version: "pulaski.lot.written_up.v1" is "lot written up".</summary>
    public static string EventName(string type)
    {
        var parts = type.Split('.');
        var words = parts.Length >= 3 && parts[0] == "pulaski" ? parts[1..^(parts[^1].StartsWith('v') ? 1 : 0)] : parts;
        return string.Join(' ', words).Replace('_', ' ');
    }

    private static string Wait(TimeSpan wait) =>
        wait < TimeSpan.FromMinutes(1) ? $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s" : $"{(int)Math.Ceiling(wait.TotalMinutes)} min";
}
