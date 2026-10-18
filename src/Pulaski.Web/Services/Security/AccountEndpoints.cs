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

        return routes;
    }
}
