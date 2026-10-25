using Pulaski.Contracts.EquipmentModel;
using Pulaski.Web.Services.Security;

namespace Pulaski.Web.Services;

/// <summary>
/// The site the site-scoped screens show (D-027), kept for the session so moving between them keeps it. Each screen offers only
/// the sites where the user holds one of its views (D-039), so a site picker never leads to a screen they can't see there; the
/// choice is kept for the screens that can show it.
/// </summary>
public sealed class SiteSelection(CurrentUser user)
{
    private IReadOnlyList<EquipmentDto> _all = [];
    private Guid? _chosen;

    /// <summary>Every active site the user can see anything at, whatever the screen: for choosing where a role applies, say.</summary>
    public IReadOnlyList<EquipmentDto> AllSites => _all;

    /// <summary>The views the current screen needs (its <see cref="RequiresViewAttribute"/>), set by the layout on each navigation; null for none.</summary>
    public IReadOnlyList<string>? ScreenViews { get; set; }

    /// <summary>The sites the current screen can show.</summary>
    public IReadOnlyList<EquipmentDto> Sites => ForScreen(_all);

    /// <summary>The chosen site, or the current screen's first site when it can't show that one.</summary>
    public Guid? SiteId
    {
        get
        {
            var sites = Sites;
            return sites.Any(s => s.Id == _chosen) ? _chosen : sites.Count > 0 ? sites[0].Id : null;
        }
        set => _chosen = value;
    }

    public EquipmentDto? Site => Sites.FirstOrDefault(s => s.Id == SiteId);

    /// <summary>Of <paramref name="sites"/>, those the current screen can show, for screens that list sites of their own.</summary>
    public IReadOnlyList<EquipmentDto> ForScreen(IEnumerable<EquipmentDto> sites) =>
        ScreenViews is not { Count: > 0 } views ? [.. sites] : [.. sites.Where(s => user.CanAny(views, s.Id))];

    /// <summary>Loads the sites once per session.</summary>
    public async Task EnsureAsync(PulaskiApiClient api, CancellationToken ct = default)
    {
        if (_all.Count == 0)
        {
            Use(await api.ListEquipmentAsync(ct: ct));
        }
    }

    /// <summary>For tests and previews: the sites from the equipment given.</summary>
    public void Use(IEnumerable<EquipmentDto> equipment) => _all = [.. equipment.Where(e => e.Level == "site" && e.IsActive)];
}
