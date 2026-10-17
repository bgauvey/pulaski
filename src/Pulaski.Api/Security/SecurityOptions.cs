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
}

internal sealed class BootstrapAdminOptions
{
    public string Username { get; set; } = "admin";

    /// <summary>A temporary password, changed at the first sign-in; no administrator is created without one.</summary>
    public string? Password { get; set; }
}
