using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Testing;

/// <summary>Label settings, printers and supplier formats (#75) in memory.</summary>
public sealed class InMemoryLabels : ILabelRepository, ILabelReadStore
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Versioned<LabelSettings>> _settings = [];
    private readonly Dictionary<Guid, Versioned<LabelPrinter>> _printers = [];
    private readonly Dictionary<Guid, Versioned<SupplierFormat>> _formats = [];
    private long _version;

    public Task<Versioned<LabelSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_settings.TryGetValue(siteId, out var settings))
            {
                settings = new(new LabelSettings(siteId, LabelFormats.Pulaski, null, 0, 1, null), ++_version);
                _settings[siteId] = settings;
            }

            return Task.FromResult(settings);
        }
    }

    public Task<long> UpdateSettingsAsync(LabelSettings settings, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(Save(_settings, settings.SiteId, settings));

    public Task<Versioned<LabelPrinter>?> GetPrinterForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Find(_printers, id));

    public Task<long> InsertPrinterAsync(LabelPrinter printer, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Save(_printers, printer.Id, printer));

    public Task<long> UpdatePrinterAsync(LabelPrinter printer, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Save(_printers, printer.Id, printer));

    public Task<Versioned<SupplierFormat>?> GetFormatForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Find(_formats, id));

    public Task<long> InsertFormatAsync(SupplierFormat format, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Save(_formats, format.Id, format));

    public Task<long> UpdateFormatAsync(SupplierFormat format, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Save(_formats, format.Id, format));

    public Task<LabelSetupDto> GetSetupAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var settings = _settings.GetValueOrDefault(siteId);
            return Task.FromResult(new LabelSetupDto
            {
                SiteId = siteId,
                LabelFormat = settings?.Value.LabelFormat ?? LabelFormats.Pulaski,
                Gs1CompanyPrefix = settings?.Value.Gs1CompanyPrefix,
                SsccExtension = settings?.Value.SsccExtension ?? 0,
                SsccNextSerial = settings?.Value.SsccNextSerial ?? 1,
                DefaultPrinterId = settings?.Value.DefaultPrinterId,
                Printers = [.. _printers.Values.Where(p => p.Value.SiteId == siteId).Select(p => Dto(p))],
                SupplierFormats = [.. _formats.Values.Where(f => f.Value.SiteId == siteId).Select(f => new SupplierBarcodeFormatDto
                {
                    Id = f.Value.Id,
                    SiteId = f.Value.SiteId,
                    Code = f.Value.Code,
                    Name = f.Value.Name,
                    Pattern = f.Value.Pattern,
                    ExpiryFormat = f.Value.ExpiryFormat,
                    SortOrder = f.Value.SortOrder,
                    IsActive = f.Value.IsActive,
                    Version = f.Version,
                })],
                Version = settings?.Version ?? 0,
            });
        }
    }

    public Task<LabelPrinterDto?> GetPrinterAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Find(_printers, id) is { } p ? Dto(p) : null);

    public Task<IReadOnlyList<SupplierFormat>> ListActiveFormatsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<SupplierFormat>>(
                [.. _formats.Values.Select(f => f.Value).Where(f => f.SiteId == siteId && f.IsActive).OrderBy(f => f.SortOrder).ThenBy(f => f.Code, StringComparer.Ordinal)]);
        }
    }

    private static LabelPrinterDto Dto(Versioned<LabelPrinter> p) => new()
    {
        Id = p.Value.Id,
        SiteId = p.Value.SiteId,
        Code = p.Value.Code,
        Name = p.Value.Name,
        Method = p.Value.Method,
        Host = p.Value.Host,
        Port = p.Value.Port,
        DropFolder = p.Value.DropFolder,
        TemplateSet = p.Value.TemplateSet,
        IsActive = p.Value.IsActive,
        Version = p.Version,
    };

    private Versioned<T>? Find<T>(Dictionary<Guid, Versioned<T>> items, Guid id)
    {
        lock (_lock)
        {
            return items.GetValueOrDefault(id);
        }
    }

    private long Save<T>(Dictionary<Guid, Versioned<T>> items, Guid id, T value)
    {
        lock (_lock)
        {
            items[id] = new(value, ++_version);
            return _version;
        }
    }
}
