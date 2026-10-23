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
