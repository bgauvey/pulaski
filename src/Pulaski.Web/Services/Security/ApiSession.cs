using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Pulaski.Web.Services.Security;

/// <summary>The signed-in user's API token, and what to do when the API no longer accepts it (D-037).</summary>
public sealed class ApiSession(AuthenticationStateProvider authentication, NavigationManager navigation)
{
    public async Task<string?> TokenAsync() =>
        (await authentication.GetAuthenticationStateAsync()).User.FindFirst(AccountSession.TokenClaim)?.Value;

    /// <summary>The session ran out, or the user was switched off: sign in again.</summary>
    public void SignInAgain() => navigation.NavigateTo("account/expired", forceLoad: true);
}
