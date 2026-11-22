using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace Pulaski.Web.Services.Localization;

/// <summary>
/// The reader's language and region (#128, D-114), kept in ASP.NET Core's culture cookie: set at sign-in from the user's choice and when they
/// change it; without one the browser's languages decide, else English (United States). The UI culture is the language, the culture the
/// region (dates and numbers).
/// </summary>
public static class Cultures
{
    /// <summary>The region a sample shows when the reader chose none; their screens then keep Pulaski's usual formats.</summary>
    public const string Default = "en-US";

    /// <summary>Every culture this server knows (and the invariant one, Pulaski's usual formats), so any language and region can be used.</summary>
    public static IList<CultureInfo> Known { get; } = [.. CultureInfo.GetCultures(CultureTypes.AllCultures)];

    public static RequestLocalizationOptions Options() => new RequestLocalizationOptions
    {
        DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en")),
        SupportedCultures = Known,
        SupportedUICultures = Known,
        FallBackToParentCultures = true,
        FallBackToParentUICultures = true,
    };

    /// <summary>Writes the cookie for a language (null: English) and region (null: the language's usual; for English, Pulaski's usual formats).</summary>
    public static void Set(HttpContext http, string? language, string? region)
    {
        var ui = Culture(language) ?? CultureInfo.GetCultureInfo("en");
        var formats = Culture(region) ?? (ui.Name == "en" ? CultureInfo.InvariantCulture : ui);
        http.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(formats, ui)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps });
    }

    /// <summary>Sets this request's cultures, for a terminal's page (its own language and region).</summary>
    public static void Apply(string? language, string? region)
    {
        var ui = Culture(language) ?? CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = ui;
        CultureInfo.CurrentCulture = Culture(region) ?? (ui.Name == "en" ? CultureInfo.InvariantCulture : ui);
    }

    private static CultureInfo? Culture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
