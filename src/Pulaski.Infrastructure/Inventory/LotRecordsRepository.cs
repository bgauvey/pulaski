using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Domain.Inventory;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Lot attributes and documents (D-056), written beside the lot so its row and history never change.</summary>
internal sealed class LotRecordsRepository(DbSession session) : ILotRecordsRepository
{
    private const string IsSite = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site')";

    private const string CodeExists = "SELECT EXISTS (SELECT 1 FROM core.lot_attribute WHERE site_id = @SiteId AND code = @Code)";

    private const string ActiveClasses = "SELECT count(*)::int FROM core.material_class WHERE id = ANY(@Ids) AND is_active";

    private const string ActiveUnit = "SELECT EXISTS (SELECT 1 FROM core.uom WHERE id = @Id AND is_active)";

    private const string GetAttribute = $"""
        SELECT {LotRecordSql.AttributeColumns} FROM core.lot_attribute a WHERE a.id = @Id FOR UPDATE
        """;

    private const string InsertAttribute = """
        INSERT INTO core.lot_attribute (id, site_id, code, name, data_type, uom_id, choices, applies_to_all, sort_order, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @DataType, @UomId, @Choices, @AppliesToAll, @SortOrder, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateAttribute = """
        UPDATE core.lot_attribute
        SET name = @Name, uom_id = @UomId, choices = @Choices, applies_to_all = @AppliesToAll, sort_order = @SortOrder, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string ApplyClasses = """
        INSERT INTO core.lot_attribute_class (attribute_id, material_class_id, is_applied)
        SELECT @Id, unnest(@ClassIds::uuid[]), true
        ON CONFLICT (attribute_id, material_class_id) DO UPDATE SET is_applied = true
        """;

    private const string TakeOffClasses = """
        UPDATE core.lot_attribute_class SET is_applied = false
        WHERE attribute_id = @Id AND is_applied AND NOT (material_class_id = ANY(@ClassIds))
        """;

    private const string GetValue = $"""
        SELECT {LotRecordSql.ValueColumns} FROM core.lot_attribute_value v WHERE v.lot_id = @LotId AND v.attribute_id = @AttributeId FOR UPDATE
        """;

    private const string SaveValue = """
        INSERT INTO core.lot_attribute_value (lot_id, attribute_id, value_text, value_number, value_date, value_boolean, updated_at, updated_by)
        VALUES (@LotId, @AttributeId, @Text, @Number, @Date, @Boolean, @At, @Actor)
        ON CONFLICT (lot_id, attribute_id) DO UPDATE
        SET value_text = EXCLUDED.value_text, value_number = EXCLUDED.value_number, value_date = EXCLUDED.value_date,
            value_boolean = EXCLUDED.value_boolean, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
        RETURNING xmin::text::bigint
        """;

    private const string InsertDocument = """
        INSERT INTO core.lot_document (id, site_id, kind, title, document_number, issuer, issued_on, url, file_name, content_type, size_bytes, sha256,
                                       status, created_at, created_by, updated_at)
        VALUES (@Id, @SiteId, @Kind, @Title, @DocumentNumber, @Issuer, @IssuedOn, @Url, @FileName, @ContentType, @SizeBytes, @Sha256Bytes,
                @Status, @CreatedAt, @CreatedBy, @At)
        RETURNING xmin::text::bigint
        """;

    private const string InsertContent = "INSERT INTO core.lot_document_content (document_id, content) VALUES (@Id, @Content)";

    private const string InsertLink = """
        INSERT INTO core.lot_document_link (document_id, lot_id, via, linked_at) VALUES (@Id, @LotId, 'attached', @At)
        """;

    private const string GetDocument = $"SELECT {LotRecordSql.DocumentColumns}, d.xmin::text::bigint AS version FROM core.lot_document d WHERE d.id = @Id FOR UPDATE";

    private const string UpdateDocument = """
        UPDATE core.lot_document
        SET status = @Status, withdrawn_at = @WithdrawnAt, withdrawn_by = @WithdrawnBy, withdraw_reason = @WithdrawReason, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsSite, new { Id = siteId }, cancellationToken);

    public Task<bool> AttributeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CodeExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public Task<int> CountActiveClassesAsync(IReadOnlyCollection<Guid> classIds, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<int>(ActiveClasses, new { Ids = classIds.ToArray() }, cancellationToken);

    public Task<bool> IsActiveUnitAsync(Guid uomId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(ActiveUnit, new { Id = uomId }, cancellationToken);

    public async Task<Versioned<LotAttributeDefinition>?> GetAttributeForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LotRecordSql.AttributeRow>(GetAttribute, new { Id = id }, cancellationToken) is { } row
            ? new Versioned<LotAttributeDefinition>(row.ToDomain(), row.Version)
            : null;

    public async Task<long> InsertAttributeAsync(LotAttributeDefinition attribute, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = await session.ExecuteScalarAsync<long>(InsertAttribute, AttributeArgs(attribute, at), cancellationToken);
        await session.ExecuteAsync(ApplyClasses, new { attribute.Id, ClassIds = attribute.ClassIds.ToArray() }, cancellationToken);
        return version;
    }

    public async Task<long> UpdateAttributeAsync(LotAttributeDefinition attribute, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var classes = new { attribute.Id, ClassIds = attribute.ClassIds.ToArray() };
        await session.ExecuteAsync(TakeOffClasses, classes, cancellationToken);
        await session.ExecuteAsync(ApplyClasses, classes, cancellationToken);
        return await session.ExecuteScalarAsync<long>(UpdateAttribute, AttributeArgs(attribute, at), cancellationToken);
    }

    public Task<LotRecordContext?> GetLotContextAsync(Guid lotId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<LotRecordContext>(LotRecordSql.LotContext, new { Id = lotId }, cancellationToken);

    public async Task<StoredAttributeValue?> GetValueForUpdateAsync(Guid lotId, Guid attributeId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<LotRecordSql.ValueRow>(GetValue, new { LotId = lotId, AttributeId = attributeId }, cancellationToken))?.ToStored();

    public Task<long> SaveValueAsync(Guid lotId, Guid attributeId, LotAttributeValue value, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            SaveValue,
            new { LotId = lotId, AttributeId = attributeId, value.Text, value.Number, value.Date, value.Boolean, Actor = actor, At = at },
            cancellationToken);

    public async Task<long> InsertDocumentAsync(LotDocument document, byte[]? content, Guid lotId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = await session.ExecuteScalarAsync<long>(InsertDocument, DocumentArgs(document, at), cancellationToken);
        if (content is not null)
        {
            await session.ExecuteAsync(InsertContent, new { document.Id, Content = content }, cancellationToken);
        }

        await session.ExecuteAsync(InsertLink, new { document.Id, LotId = lotId, At = at }, cancellationToken);
        return version;
    }

    public async Task<Versioned<LotDocument>?> GetDocumentForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LotRecordSql.DocumentRow>(GetDocument, new { Id = id }, cancellationToken) is { } row
            ? new Versioned<LotDocument>(row.ToDomain(), row.Version)
            : null;

    public Task<long> UpdateDocumentAsync(LotDocument document, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateDocument, DocumentArgs(document, at), cancellationToken);

    private static object AttributeArgs(LotAttributeDefinition a, DateTimeOffset at) => new
    {
        a.Id,
        a.SiteId,
        a.Code,
        a.Name,
        a.DataType,
        a.UomId,
        Choices = a.Choices.ToArray(),
        a.AppliesToAll,
        a.SortOrder,
        a.IsActive,
        At = at,
    };

    private static object DocumentArgs(LotDocument d, DateTimeOffset at) => new
    {
        d.Id,
        d.SiteId,
        d.Kind,
        d.Title,
        d.DocumentNumber,
        d.Issuer,
        d.IssuedOn,
        d.Url,
        d.FileName,
        d.ContentType,
        d.SizeBytes,
        Sha256Bytes = d.Sha256 is { } hex ? Convert.FromHexString(hex) : null,
        d.Status,
        d.WithdrawnAt,
        d.WithdrawnBy,
        d.WithdrawReason,
        d.CreatedAt,
        d.CreatedBy,
        At = at,
    };
}
