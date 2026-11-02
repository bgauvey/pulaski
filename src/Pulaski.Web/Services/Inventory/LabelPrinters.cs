using Pulaski.Contracts.Inventory;

namespace Pulaski.Web.Services.Inventory;

/// <summary>A site's active label printers and its default (#75), loaded once per connection, for print buttons on lists.</summary>
public sealed class LabelPrinters(PulaskiApiClient api)
{
    private readonly Dictionary<Guid, Task<LabelSetupDto>> _setups = [];

    public async Task<(IReadOnlyList<LabelPrinterDto> Printers, Guid? Default)> GetAsync(Guid siteId)
    {
        if (!_setups.TryGetValue(siteId, out var setup))
        {
            setup = api.GetLabelSetupAsync(siteId);
            _setups[siteId] = setup;
        }

        try
        {
            var loaded = await setup;
            return ([.. loaded.Printers.Where(p => p.IsActive)], loaded.DefaultPrinterId);
        }
        catch (ApiException)
        {
            _setups.Remove(siteId);
            return ([], null);
        }
    }
}
