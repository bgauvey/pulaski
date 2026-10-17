using Pulaski.Contracts.EquipmentModel;

namespace Pulaski.Web.Services;

/// <summary>
/// The site the inventory screens show (D-027), kept for the session so moving between them keeps
/// it. Lots belong to a site; the screens default to the first active one.
/// </summary>
public sealed class SiteSelection
{
    public IReadOnlyList<EquipmentDto> Sites { get; private set; } = [];

    public Guid? SiteId { get; set; }

    public EquipmentDto? Site => Sites.FirstOrDefault(s => s.Id == SiteId);

    /// <summary>Loads the sites once per session and picks the first active one if none is chosen.</summary>
    public async Task EnsureAsync(PulaskiApiClient api, CancellationToken ct = default)
    {
        if (Sites.Count == 0)
        {
            Sites = [.. (await api.ListEquipmentAsync(ct: ct)).Where(e => e.Level == "site" && e.IsActive)];
        }

        if (SiteId is null || Site is null)
        {
            SiteId = Sites.Count > 0 ? Sites[0].Id : null;
        }
    }
}
