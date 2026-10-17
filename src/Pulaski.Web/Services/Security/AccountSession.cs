using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Pulaski.Contracts.Security;

namespace Pulaski.Web.Services.Security;

/// <summary>
/// The browser's session (D-037): a cookie holding who signed in and the API's bearer token, which the
/// UI passes on with every API call. The cookie ends when the token does.
/// </summary>
public static class AccountSession
{
    public const string TokenClaim = "pulaski_token";
    public const string DisplayNameClaim = "pulaski_display_name";
    public const string MustChangeClaim = "pulaski_must_change";
    public const string LoginPath = "/account/login";
    public const string PasswordPath = "/account/password";

    public static Task SignInAsync(HttpContext http, SignInResponse session) =>
        SignInAsync(http, session.Token, session.ExpiresAt, session.User.Id, session.User.Username, session.User.DisplayName, session.User.MustChangePassword);

    /// <summary>The same session, now that its temporary password has been replaced.</summary>
    public static async Task PasswordChangedAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var user = result.Principal ?? http.User;
        await SignInAsync(
            http,
            user.FindFirstValue(TokenClaim) ?? "",
            result.Properties?.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(1),
            Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty,
            user.Identity?.Name ?? "",
            user.FindFirstValue(DisplayNameClaim) ?? "",
            mustChange: false);
    }

    public static Task SignOutAsync(HttpContext http) => http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    /// <summary>Only a path on this site, so a crafted link can't send someone elsewhere after signing in.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            && !returnUrl.StartsWith("/\\", StringComparison.Ordinal) && !returnUrl.StartsWith("/account/", StringComparison.OrdinalIgnoreCase)
            ? returnUrl
            : "/";

    private static Task SignInAsync(HttpContext http, string token, DateTimeOffset expiresAt, Guid userId, string username, string displayName, bool mustChange)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new(DisplayNameClaim, displayName),
            new(TokenClaim, token),
        };
        if (mustChange)
        {
            claims.Add(new Claim(MustChangeClaim, "true"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { ExpiresUtc = expiresAt, IsPersistent = false, AllowRefresh = false });
    }
}
