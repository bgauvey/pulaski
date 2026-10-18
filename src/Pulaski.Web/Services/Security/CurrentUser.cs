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

    /// <summary>Whether the user holds any of <paramref name="permissions"/>, at <paramref name="siteId"/> or (without one) at any site.</summary>
    public bool CanAny(IEnumerable<string> permissions, Guid? siteId = null) => permissions.Any(p => Can(p, siteId));

    /// <summary>Whether the user may see any screen at all (D-039). The API lists views a change brings with it too.</summary>
    public bool CanSeeAnything => Me is not null && (CanAny(PermissionCodes.Views) || Can(PermissionCodes.Security));

    /// <summary>The menu's screens in order, each with the view it needs (D-039).</summary>
    public static IReadOnlyList<(string Href, string Permission)> Screens { get; } =
    [
        ("", PermissionCodes.OverviewView),
        ("work-orders", PermissionCodes.WorkOrdersView),
        ("schedule", PermissionCodes.ScheduleView),
        ("floor", PermissionCodes.FloorView),
        ("inventory", PermissionCodes.InventoryView),
        ("labor", PermissionCodes.LaborView),
        ("equipment", PermissionCodes.EquipmentView),
        ("materials", PermissionCodes.MaterialsView),
        ("processes", PermissionCodes.ProcessesView),
        ("units", PermissionCodes.UnitsView),
        ("reasons", PermissionCodes.ReasonsView),
        ("shifts", PermissionCodes.ShiftsView),
        ("people", PermissionCodes.People),
        ("users", PermissionCodes.Security),
    ];

    /// <summary>The first screen in the menu the user may see; null when there is none.</summary>
    public string? FirstScreen => Screens.FirstOrDefault(s => Can(s.Permission)) is { Permission: not null } first ? first.Href : null;

    /// <summary>The roles as one line, such as "Supervisor at PLANT1, Quality at PLANT2".</summary>
    public string RoleSummary => Me is null
        ? ""
        : string.Join(", ", Me.Roles.Select(r => r.SiteCode is null ? r.RoleName : $"{r.RoleName} at {r.SiteCode}"));
}
