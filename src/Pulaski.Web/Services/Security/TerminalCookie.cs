namespace Pulaski.Web.Services.Security;

/// <summary>
/// A shop-floor terminal's secret (D-049), kept in an HttpOnly cookie on that device after it is enrolled. It is the
/// terminal's credential: the browser sends it to Pulaski.Web, which passes it on to the API and never to the page.
/// </summary>
public static class TerminalCookie
{
    public const string Name = ".Pulaski.Terminal";
    public const string EnrolPath = "/terminal/enrol";
    public const string HomePath = "/terminal";

    /// <summary>The line display board (#120), under the cookie's path so the device's secret is sent.</summary>
    public const string BoardPath = "/terminal/board";

    /// <summary>The operator station (#146) and the cookie that keeps its person signed on between screens.</summary>
    public const string StationPath = "/terminal/station";
    public const string StationName = ".Pulaski.Station";

    public static string? GetStation(HttpContext http) =>
        http.Request.Cookies.TryGetValue(StationName, out var ticket) && !string.IsNullOrWhiteSpace(ticket) ? ticket : null;

    /// <summary>Kept until the ticket runs out: the station then waits for a badge again.</summary>
    public static void SetStation(HttpContext http, string ticket, DateTimeOffset expiresAt) => http.Response.Cookies.Append(StationName, ticket, new CookieOptions
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = HomePath,
        Expires = expiresAt,
    });

    public static void ClearStation(HttpContext http) => http.Response.Cookies.Delete(StationName, new CookieOptions { Path = HomePath });

    public static string? Get(HttpContext http) => http.Request.Cookies.TryGetValue(Name, out var secret) && !string.IsNullOrWhiteSpace(secret) ? secret : null;

    public static void Set(HttpContext http, string secret) => http.Response.Cookies.Append(Name, secret, new CookieOptions
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = HomePath,
        Expires = DateTimeOffset.UtcNow.AddDays(400),
    });
}
