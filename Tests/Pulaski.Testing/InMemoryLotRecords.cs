using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Testing;

/// <summary>In-memory lot attributes and documents (D-056); lots themselves come from <see cref="InMemoryInventory"/>.</summary>
public sealed class InMemoryLotRecords(InMemoryInventory inventory) : ILotRecordsRepository, ILotRecordsReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<LotAttributeDefinition>> _attributes = [];
    private readonly Dictionary<(Guid Lot, Guid Attribute), StoredAttributeValue> _values = [];
    private readonly Dictionary<Guid, Versioned<LotDocument>> _documents = [];
    private readonly Dictionary<Guid, byte[]> _contents = [];
    private readonly List<(Guid Document, Guid Lot)> _links = [];
    private long _next = 1;

    public HashSet<Guid> Sites { get; } = [];

    /// <summary>Material classes that exist and are active.</summary>
    public HashSet<Guid> Classes { get; } = [];

    public HashSet<Guid> Units { get; } = [];

    /// <summary>Each material's class, as a test sets it.</summary>
    public Dictionary<Guid, Guid?> MaterialClasses { get; } = [];

    public LotDocument Document(Guid id)
    {
        lock (_lock)
        {
            return _documents[id].Value;
        }
    }

    public byte[]? ContentOf(Guid id)
    {
        lock (_lock)
        {
            return _contents.GetValueOrDefault(id);
        }
    }

    // ---- ILotRecordsRepository ----

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) => Locked(() => Sites.Contains(siteId));

    public Task<bool> AttributeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        Locked(() => _attributes.Values.Any(a => a.Value.SiteId == siteId && a.Value.Code == code));

    public Task<int> CountActiveClassesAsync(IReadOnlyCollection<Guid> classIds, CancellationToken cancellationToken) =>
        Locked(() => classIds.Count(Classes.Contains));

    public Task<bool> IsActiveUnitAsync(Guid uomId, CancellationToken cancellationToken) => Locked(() => Units.Contains(uomId));

    public Task<Versioned<LotAttributeDefinition>?> GetAttributeForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        Locked(() => _attributes.GetValueOrDefault(id));

    public Task<long> InsertAttributeAsync(LotAttributeDefinition attribute, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(attribute));

    public Task<long> UpdateAttributeAsync(LotAttributeDefinition attribute, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(attribute));

    public Task<LotRecordContext?> GetLotContextAsync(Guid lotId, CancellationToken cancellationToken) =>
        Locked(() => inventory.Find(lotId)?.Value is { } lot ? new LotRecordContext(lot.SiteId, lot.MaterialId, MaterialClasses.GetValueOrDefault(lot.MaterialId)) : null);

    public Task<StoredAttributeValue?> GetValueForUpdateAsync(Guid lotId, Guid attributeId, CancellationToken cancellationToken) =>
        Locked(() => _values.GetValueOrDefault((lotId, attributeId)));

    public Task<long> SaveValueAsync(Guid lotId, Guid attributeId, LotAttributeValue value, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            var version = ++_next;
            _values[(lotId, attributeId)] = new StoredAttributeValue(lotId, attributeId, value, at, actor, version);
            return version;
        });

    public Task<long> InsertDocumentAsync(LotDocument document, byte[]? content, Guid lotId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            var version = ++_next;
            _documents[document.Id] = new(document, version);
            if (content is not null)
            {
                _contents[document.Id] = content;
            }

            _links.Add((document.Id, lotId));
            return version;
        });

    public Task<Versioned<LotDocument>?> GetDocumentForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _documents.GetValueOrDefault(id));

    public Task<long> UpdateDocumentAsync(LotDocument document, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            var version = ++_next;
            _documents[document.Id] = new(document, version);
            return version;
        });

    // ---- ILotRecordsReadStore ----

    public Task<IReadOnlyList<LotAttributeDto>> ListAttributesAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<LotAttributeDto>>(() => [.. _attributes.Values.Where(a => a.Value.SiteId == siteId)
            .OrderByDescending(a => a.Value.IsActive).ThenBy(a => a.Value.SortOrder).ThenBy(a => a.Value.Code, StringComparer.Ordinal)
            .Select(a => new LotAttributeDto
            {
                Id = a.Value.Id,
                SiteId = a.Value.SiteId,
                Code = a.Value.Code,
                Name = a.Value.Name,
                DataType = a.Value.DataType,
                UomId = a.Value.UomId,
                Choices = a.Value.Choices,
                AppliesToAll = a.Value.AppliesToAll,
                ClassIds = a.Value.ClassIds,
                SortOrder = a.Value.SortOrder,
                IsActive = a.Value.IsActive,
                LotsWithValue = _values.Values.Count(v => v.AttributeId == a.Value.Id && !v.Value.IsEmpty),
                Version = a.Version,
            })]);

    public Task<LotAttributeDefinition?> GetAttributeAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _attributes.GetValueOrDefault(id)?.Value);

    public Task<IReadOnlyList<StoredAttributeValue>> ListValuesAsync(Guid lotId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<StoredAttributeValue>>(() => [.. _values.Values.Where(v => v.LotId == lotId)]);

    public Task<IReadOnlyList<LinkedDocument>> ListDocumentsAsync(Guid lotId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<LinkedDocument>>(() => [.. _links.Where(l => l.Lot == lotId)
            .Select(l => new LinkedDocument(_documents[l.Document].Value, "attached", null, null, _documents[l.Document].Version))]);

    public Task<LotDocumentContent?> GetContentAsync(Guid documentId, CancellationToken cancellationToken) =>
        Locked(() => _contents.TryGetValue(documentId, out var content) && _documents[documentId].Value is { FileName: { } name, ContentType: { } type } d
            ? new LotDocumentContent(d.SiteId, name, type, content)
            : null);

    private long Save(LotAttributeDefinition attribute)
    {
        var version = ++_next;
        _attributes[attribute.Id] = new(attribute, version);
        return version;
    }

    private Task<T> Locked<T>(Func<T> read)
    {
        lock (_lock)
        {
            return Task.FromResult(read());
        }
    }
}
