namespace Pulaski.Web.Services;

/// <summary>
/// How a screen that refreshes itself says how fresh it is (the Overview's rule, D-070): every 30 seconds, stale once a minute has passed
/// without a successful load, when it keeps the last figures it had.
/// </summary>
public static class LiveRefresh
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    public static bool IsStale(DateTimeOffset? loadedAt, DateTimeOffset now) => loadedAt is { } at && now - at > StaleAfter;

    /// <summary>"Live, updated 14:03" or "Stale since 14:03", in the site's time.</summary>
    public static string Status(DateTimeOffset? loadedAt, DateTimeOffset now, string? timeZone) => loadedAt switch
    {
        null => "Connecting…",
        { } at when IsStale(at, now) => $"Stale since {Time(at, timeZone)}",
        { } at => $"Live, updated {Time(at, timeZone)}",
    };

    private static string Time(DateTimeOffset at, string? timeZone) =>
        Display.ToSite(at, timeZone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
