using Pulaski.Contracts.Security;

namespace Pulaski.Web.Services.Security;

/// <summary>
/// The signed-in user and what they may do (D-037), loaded once per session. Screens use it to show only the
/// actions the user may take; the API checks every change regardless.
/// </summary>
public sealed class CurrentUser
{
    public MeDto? Me { get; private set; }

    public string DisplayName => Me?.DisplayName ?? "";

    public bool IsAdministrator => Me?.Roles.Any(r => r.RoleCode == "administrator") == true;

    public async Task EnsureAsync(PulaskiApiClient api, CancellationToken ct = default) => Me ??= await api.GetMeAsync(ct);

    /// <summary>For tests and previews: the user as given.</summary>
    public void Set(MeDto me) => Me = me;

    /// <summary>Whether the user holds <paramref name="permission"/> at <paramref name="siteId"/>; without a site, at any site.</summary>
    public bool Can(string permission, Guid? siteId = null) =>
        Me?.Permissions.FirstOrDefault(p => p.Code == permission) is { } grant
        && (grant.SiteIds is null || siteId is null || grant.SiteIds.Contains(siteId.Value));

    /// <summary>The roles as one line, such as "Supervisor at PLANT1, Quality at PLANT2".</summary>
    public string RoleSummary => Me is null
        ? ""
        : string.Join(", ", Me.Roles.Select(r => r.SiteCode is null ? r.RoleName : $"{r.RoleName} at {r.SiteCode}"));
}
