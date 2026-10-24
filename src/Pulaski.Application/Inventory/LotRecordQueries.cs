using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>A site's lot attribute definitions (D-056), switched-off ones too.</summary>
public sealed record ListLotAttributesQuery(Guid SiteId) : IQuery<IReadOnlyList<LotAttributeDto>>;

/// <summary>A lot's attributes and documents (D-056).</summary>
public sealed record GetLotRecordsQuery(Guid LotId) : IQuery<LotRecordsDto>;

/// <summary>A stored document's file (D-056).</summary>
public sealed record GetLotDocumentContentQuery(Guid DocumentId) : IQuery<LotDocumentContent>;

internal sealed class ListLotAttributesHandler(ILotRecordsReadStore store) : IQueryHandler<ListLotAttributesQuery, IReadOnlyList<LotAttributeDto>>
{
    public Task<IReadOnlyList<LotAttributeDto>> HandleAsync(ListLotAttributesQuery query, CancellationToken cancellationToken) =>
        store.ListAttributesAsync(query.SiteId, cancellationToken);
}

internal sealed class GetLotRecordsHandler(ILotRecordsReadStore store) : IQueryHandler<GetLotRecordsQuery, LotRecordsDto>
{
    public async Task<LotRecordsDto> HandleAsync(GetLotRecordsQuery query, CancellationToken cancellationToken)
    {
        var lot = await store.GetLotContextAsync(query.LotId, cancellationToken) ?? throw new NotFoundException($"Lot {query.LotId} not found.");
        var all = (await store.ListValuesAsync(query.LotId, cancellationToken)).ToDictionary(v => v.AttributeId);
        var values = all.Values.Where(v => !v.Value.IsEmpty).ToDictionary(v => v.AttributeId);

        // What its material may have, and anything it has a value of even if no longer offered.
        var attributes = (await store.ListAttributesAsync(lot.SiteId, cancellationToken))
            .Select(a => (Attribute: a, CanSet: a.IsActive && (a.AppliesToAll || (lot.MaterialClassId is { } c && a.ClassIds.Contains(c)))))
            .Where(a => a.CanSet || values.ContainsKey(a.Attribute.Id))
            .Select(a => new LotAttributeValueDto
            {
                AttributeId = a.Attribute.Id,
                Code = a.Attribute.Code,
                Name = a.Attribute.Name,
                DataType = a.Attribute.DataType,
                UomCode = a.Attribute.UomCode,
                Choices = a.Attribute.Choices,
                Value = all.GetValueOrDefault(a.Attribute.Id)?.Value.Display,
                UpdatedAt = all.GetValueOrDefault(a.Attribute.Id)?.UpdatedAt,
                UpdatedBy = all.GetValueOrDefault(a.Attribute.Id)?.UpdatedBy,
                CanSet = a.CanSet,
                Version = all.GetValueOrDefault(a.Attribute.Id)?.Version ?? 0,
            })
            .ToList();

        var documents = (await store.ListDocumentsAsync(query.LotId, cancellationToken))
            .OrderBy(d => d.Document.Status == Domain.Inventory.LotDocument.Current ? 0 : 1)
            .ThenByDescending(d => d.Document.CreatedAt)
            .Select(d => new LotDocumentDto
            {
                Id = d.Document.Id,
                Kind = d.Document.Kind,
                Title = d.Document.Title,
                DocumentNumber = d.Document.DocumentNumber,
                Issuer = d.Document.Issuer,
                IssuedOn = d.Document.IssuedOn,
                Url = d.Document.Url,
                FileName = d.Document.FileName,
                ContentType = d.Document.ContentType,
                SizeBytes = d.Document.SizeBytes,
                Sha256 = d.Document.Sha256,
                Status = d.Document.Status,
                WithdrawnAt = d.Document.WithdrawnAt,
                WithdrawnBy = d.Document.WithdrawnBy,
                WithdrawReason = d.Document.WithdrawReason,
                CreatedAt = d.Document.CreatedAt,
                CreatedBy = d.Document.CreatedBy,
                Via = d.Via,
                FromLotId = d.FromLotId,
                FromLotNumber = d.FromLotNumber,
                Version = d.Version,
            })
            .ToList();

        return new LotRecordsDto { LotId = query.LotId, Attributes = attributes, Documents = documents };
    }
}

internal sealed class GetLotDocumentContentHandler(ILotRecordsReadStore store) : IQueryHandler<GetLotDocumentContentQuery, LotDocumentContent>
{
    public async Task<LotDocumentContent> HandleAsync(GetLotDocumentContentQuery query, CancellationToken cancellationToken) =>
        await store.GetContentAsync(query.DocumentId, cancellationToken)
            ?? throw new NotFoundException($"Document {query.DocumentId} has no stored file.");
}
