using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>A lot's recorded value of an attribute (D-056).</summary>
public sealed record StoredAttributeValue(Guid LotId, Guid AttributeId, LotAttributeValue Value, DateTimeOffset UpdatedAt, string UpdatedBy, long Version);

/// <summary>A document as it applies to one lot: attached to it, or carried to it by a split or merge.</summary>
public sealed record LinkedDocument(LotDocument Document, string Via, Guid? FromLotId, string? FromLotNumber, long Version);

/// <summary>A stored document's file.</summary>
public sealed record LotDocumentContent(Guid SiteId, string FileName, string ContentType, byte[] Content);

/// <summary>A lot as its records need it: its site and its material's class.</summary>
public sealed record LotRecordContext(Guid SiteId, Guid MaterialId, Guid? MaterialClassId);

/// <summary>Write side for lot attributes and documents (D-056); runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ILotRecordsRepository
{
    Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<bool> AttributeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    /// <summary>How many of these are active material classes.</summary>
    Task<int> CountActiveClassesAsync(IReadOnlyCollection<Guid> classIds, CancellationToken cancellationToken);

    Task<bool> IsActiveUnitAsync(Guid uomId, CancellationToken cancellationToken);

    Task<Versioned<LotAttributeDefinition>?> GetAttributeForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Inserts the definition and the classes it applies to.</summary>
    Task<long> InsertAttributeAsync(LotAttributeDefinition attribute, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Updates the definition; classes no longer in it are taken off (<c>is_applied = false</c>), never deleted.</summary>
    Task<long> UpdateAttributeAsync(LotAttributeDefinition attribute, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The lot's site and material class; null when it doesn't exist.</summary>
    Task<LotRecordContext?> GetLotContextAsync(Guid lotId, CancellationToken cancellationToken);

    Task<StoredAttributeValue?> GetValueForUpdateAsync(Guid lotId, Guid attributeId, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces the lot's value of the attribute; returns its new version.</summary>
    Task<long> SaveValueAsync(Guid lotId, Guid attributeId, LotAttributeValue value, string actor, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Inserts the document, its file when it has one, and its link to the lot it is attached to.</summary>
    Task<long> InsertDocumentAsync(LotDocument document, byte[]? content, Guid lotId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<LotDocument>?> GetDocumentForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> UpdateDocumentAsync(LotDocument document, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side for lot attributes and documents (D-056).</summary>
public interface ILotRecordsReadStore
{
    /// <summary>The site's definitions, switched-off ones too, in their order.</summary>
    Task<IReadOnlyList<LotAttributeDto>> ListAttributesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<LotRecordContext?> GetLotContextAsync(Guid lotId, CancellationToken cancellationToken);

    Task<LotAttributeDefinition?> GetAttributeAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredAttributeValue>> ListValuesAsync(Guid lotId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LinkedDocument>> ListDocumentsAsync(Guid lotId, CancellationToken cancellationToken);

    Task<LotDocumentContent?> GetContentAsync(Guid documentId, CancellationToken cancellationToken);
}
