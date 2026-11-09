using Pulaski.Contracts.Security;

namespace Pulaski.Web.Services.Security;

/// <summary>Signing out (D-037). Signing in and changing a password are pages under <c>/account</c>.</summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/account/logout", async (HttpContext http) =>
        {
            await AccountSession.SignOutAsync(http);
            return Results.LocalRedirect(AccountSession.LoginPath);
        });

        // The API stopped accepting the session: it ran out, or the user was switched off.
        routes.MapGet("/account/expired", async (HttpContext http) =>
        {
            await AccountSession.SignOutAsync(http);
            return Results.LocalRedirect(AccountSession.LoginPath + "?expired=true");
        });

        // Signing in through an identity provider (D-091): the API makes the provider's address and a sealed transaction, kept here in a
        // short-lived cookie until the provider sends the browser back.
        routes.MapGet("/account/oidc/{provider}", async (string provider, string? returnUrl, HttpContext http, PulaskiApiClient api) =>
        {
            var callback = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{CallbackPath}";
            try
            {
                var started = await api.StartProviderSignInAsync(provider, new StartProviderSignInRequest { RedirectUri = callback }, http.RequestAborted);
                http.Response.Cookies.Append(TransactionCookie, $"{provider}|{AccountSession.SafeReturnUrl(returnUrl)}|{started.Transaction}", new CookieOptions
                {
                    HttpOnly = true,
                    Secure = http.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromMinutes(10),
                    Path = CallbackPath,
                });
                return Results.Redirect(started.AuthorizationUrl);
            }
            catch (ApiException ex)
            {
                return Problem(http, ex.Detail ?? ex.Title);
            }
        });

        routes.MapGet(CallbackPath, async (string? code, string? state, string? error, string? error_description, HttpContext http, PulaskiApiClient api) =>
        {
            var kept = http.Request.Cookies[TransactionCookie]?.Split('|', 3);
            http.Response.Cookies.Delete(TransactionCookie, new CookieOptions { Path = CallbackPath });
            if (kept is not [var provider, var returnUrl, var transaction])
            {
                return Problem(http, "The sign-in took too long or started elsewhere. Start again.");
            }

            if (error is not null)
            {
                return Problem(http, $"The identity provider didn't sign you in ({error}{(error_description is null ? "" : $": {error_description}")}).");
            }

            try
            {
                var session = await api.CompleteProviderSignInAsync(
                    provider, new CompleteProviderSignInRequest { Code = code ?? "", State = state ?? "", Transaction = transaction }, http.RequestAborted);
                await AccountSession.SignInAsync(http, session);
                return Results.LocalRedirect(AccountSession.SafeReturnUrl(returnUrl));
            }
            catch (ApiException ex)
            {
                return Problem(http, ex.Detail ?? ex.Title);
            }
        });

        return routes;
    }

    public const string CallbackPath = "/account/oidc/callback";

    /// <summary>Why a provider sign-in didn't work, for the sign-in page to say once.</summary>
    public const string ProblemCookie = ".Pulaski.SignInProblem";

    private const string TransactionCookie = ".Pulaski.Oidc";

    // The message goes in a cookie read once, not the address, so a crafted link can't put words on the sign-in page.
    private static IResult Problem(HttpContext http, string message)
    {
        http.Response.Cookies.Append(ProblemCookie, message.Length <= 500 ? message : message[..500], new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(2),
            Path = AccountSession.LoginPath,
        });
        return Results.LocalRedirect(AccountSession.LoginPath);
    }
}
