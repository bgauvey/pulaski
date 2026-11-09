namespace Pulaski.Api.Security;

/// <summary>
/// Sign-in settings (D-037), from the <c>Security</c> section. Local sign-in issues HMAC-signed JWTs; an
/// identity provider (Okta, Entra ID, Keycloak…) will be another bearer scheme validated the same way.
/// </summary>
internal sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>The issuer and audience of Pulaski's own tokens.</summary>
    public const string Audience = "pulaski";

    /// <summary>At least 32 characters; set per deployment and kept secret.</summary>
    public string SigningKey { get; set; } = "";

    /// <summary>How long a sign-in lasts: a long shift by default.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The administrator created when the database has no users at all.</summary>
    public BootstrapAdminOptions BootstrapAdmin { get; set; } = new();

    /// <summary>OpenID Connect identity providers by name (D-091): <c>Security:Providers:{name}</c>. The name is in the sign-in URL.</summary>
    public Dictionary<string, IdentityProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>everyone</c> (default), or <c>administrators</c>: only administrators and service accounts sign in with a password (D-091).</summary>
    public string LocalSignIn { get; set; } = LocalSignInModes.Everyone;

    /// <summary>Password reset by e-mail (D-093), off by default; it also needs <see cref="EmailOptions"/>.</summary>
    public PasswordResetOptions PasswordReset { get; set; } = new();

    public bool LocalSignInForAdministratorsOnly => string.Equals(LocalSignIn, LocalSignInModes.Administrators, StringComparison.OrdinalIgnoreCase);
}

internal static class LocalSignInModes
{
    public const string Everyone = "everyone";
    public const string Administrators = "administrators";
}

/// <summary>
/// One OpenID Connect provider (D-091): Entra ID, AD FS, Keycloak, Okta or any other that publishes its discovery document. Pulaski is a
/// confidential client using the authorization code flow with PKCE; the secret stays in configuration, never in the database.
/// </summary>
internal sealed class IdentityProviderOptions
{
    /// <summary>For the sign-in button, such as "Contoso (Entra ID)".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The issuer address; its <c>/.well-known/openid-configuration</c> is read. For Entra ID, the tenant's, never <c>common</c>.</summary>
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string Scopes { get; set; } = "openid profile email";

    /// <summary>The ID token claim holding the person's name.</summary>
    public string NameClaim { get; set; } = "name";

    /// <summary>The ID token claim holding their e-mail; Entra ID may need <c>preferred_username</c> or <c>upn</c>.</summary>
    public string EmailClaim { get; set; } = "email";

    public bool IsUsable => Authority.Length > 0 && ClientId.Length > 0 && ClientSecret.Length > 0;
}

internal sealed class BootstrapAdminOptions
{
    public string Username { get; set; } = "admin";

    /// <summary>A temporary password, changed at the first sign-in; no administrator is created without one.</summary>
    public string? Password { get; set; }
}

/// <summary><c>Security:PasswordReset</c> (D-093).</summary>
internal sealed class PasswordResetOptions
{
    public bool Enabled { get; set; }

    /// <summary>How long a link works: 30 minutes by default, 5 to 1440.</summary>
    public int LinkMinutes { get; set; } = 30;

    /// <summary>The UI's public address the link is made from, such as <c>https://mes.example.com</c>: never the request's host.</summary>
    public string UiBaseUrl { get; set; } = "";
}

/// <summary>The SMTP server Pulaski sends e-mail through (D-093), from the <c>Email</c> section; never the database.</summary>
internal sealed class EmailOptions
{
    public const string Section = "Email";

    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    /// <summary>STARTTLS (or TLS on 465); off only for a relay on this machine.</summary>
    public bool UseTls { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>The sender address, such as <c>pulaski@example.com</c>.</summary>
    public string From { get; set; } = "";

    public string FromName { get; set; } = "Pulaski";

    public bool IsUsable => Host.Length > 0 && From.Contains('@', StringComparison.Ordinal);
}
