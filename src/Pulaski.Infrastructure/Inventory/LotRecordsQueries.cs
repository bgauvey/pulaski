using Dapper;
using Npgsql;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>SQL and rows shared by the lot records repository and read store (D-056).</summary>
internal static class LotRecordSql
{
    public const string AttributeColumns = """
        a.id, a.site_id, a.code, a.name, a.data_type, a.uom_id, a.choices, a.applies_to_all, a.sort_order, a.is_active,
        ARRAY(SELECT c.material_class_id FROM core.lot_attribute_class c WHERE c.attribute_id = a.id AND c.is_applied ORDER BY c.material_class_id) AS class_ids,
        a.xmin::text::bigint AS version
        """;

    public const string ValueColumns = """
        v.lot_id, v.attribute_id, v.value_text, v.value_number, v.value_date, v.value_boolean, v.updated_at, v.updated_by, v.xmin::text::bigint AS version
        """;

    public const string DocumentColumns = """
        d.id, d.site_id, d.kind, d.title, d.document_number, d.issuer, d.issued_on, d.url, d.file_name, d.content_type, d.size_bytes,
        d.sha256 AS sha256_bytes, d.status, d.withdrawn_at, d.withdrawn_by, d.withdraw_reason, d.created_at, d.created_by
        """;

    public const string LotContext = """
        SELECT l.site_id AS siteid, l.material_id AS materialid, m.material_class_id AS materialclassid
        FROM core.lot l JOIN core.material m ON m.id = l.material_id
        WHERE l.id = @Id
        """;

    internal sealed class AttributeRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string DataType { get; init; } = "";
        public Guid? UomId { get; init; }
        public string[] Choices { get; init; } = [];
        public bool AppliesToAll { get; init; }
        public int SortOrder { get; init; }
        public bool IsActive { get; init; }
        public Guid[] ClassIds { get; init; } = [];
        public long Version { get; init; }

        public LotAttributeDefinition ToDomain() =>
            new(Id, SiteId, Code, Name, DataType, UomId, Choices, AppliesToAll, ClassIds, SortOrder, IsActive);
    }

    internal sealed class ValueRow
    {
        public Guid LotId { get; init; }
        public Guid AttributeId { get; init; }
        public string? ValueText { get; init; }
        public decimal? ValueNumber { get; init; }
        public DateOnly? ValueDate { get; init; }
        public bool? ValueBoolean { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public string UpdatedBy { get; init; } = "";
        public long Version { get; init; }

        public StoredAttributeValue ToStored() =>
            new(LotId, AttributeId, new LotAttributeValue(ValueText, ValueNumber, ValueDate, ValueBoolean), UpdatedAt, UpdatedBy, Version);
    }

    internal sealed class DocumentRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Kind { get; init; } = "";
        public string Title { get; init; } = "";
        public string? DocumentNumber { get; init; }
        public string? Issuer { get; init; }
        public DateOnly? IssuedOn { get; init; }
        public string? Url { get; init; }
        public string? FileName { get; init; }
        public string? ContentType { get; init; }
        public int? SizeBytes { get; init; }
        public byte[]? Sha256Bytes { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? WithdrawnAt { get; init; }
        public string? WithdrawnBy { get; init; }
        public string? WithdrawReason { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string CreatedBy { get; init; } = "";
        public string Via { get; init; } = "";
        public Guid? FromLotId { get; init; }
        public string? FromLotNumber { get; init; }
        public long Version { get; init; }

        public LotDocument ToDomain() => new(
            Id, SiteId, Kind, Title, DocumentNumber, Issuer, IssuedOn, Url, FileName, ContentType, SizeBytes,
            Sha256Bytes is { } hash ? Convert.ToHexStringLower(hash) : null, Status, WithdrawnAt, WithdrawnBy, WithdrawReason, CreatedAt, CreatedBy);
    }
}

/// <summary>Read side for lot attributes and documents (D-056).</summary>
internal sealed class LotRecordsQueries(NpgsqlDataSource dataSource) : ILotRecordsReadStore
{
    private const string Attributes = $"""
        SELECT {LotRecordSql.AttributeColumns}, u.code AS uom_code,
               ARRAY(SELECT mc.code FROM core.lot_attribute_class c JOIN core.material_class mc ON mc.id = c.material_class_id
                     WHERE c.attribute_id = a.id AND c.is_applied ORDER BY mc.code) AS class_codes,
               (SELECT count(*)::int FROM core.lot_attribute_value v
                WHERE v.attribute_id = a.id AND num_nonnulls(v.value_text, v.value_number, v.value_date, v.value_boolean) > 0) AS lots_with_value
        FROM core.lot_attribute a
        LEFT JOIN core.uom u ON u.id = a.uom_id
        WHERE a.site_id = @SiteId
        ORDER BY a.is_active DESC, a.sort_order, a.code
        """;

    private const string Attribute = $"SELECT {LotRecordSql.AttributeColumns} FROM core.lot_attribute a WHERE a.id = @Id";

    private const string Values = $"SELECT {LotRecordSql.ValueColumns} FROM core.lot_attribute_value v WHERE v.lot_id = @Id";

    private const string Documents = $"""
        SELECT {LotRecordSql.DocumentColumns}, k.via, k.from_lot_id, f.lot_number AS from_lot_number, d.xmin::text::bigint AS version
        FROM core.lot_document_link k
        JOIN core.lot_document d ON d.id = k.document_id
        LEFT JOIN core.lot f ON f.id = k.from_lot_id
        WHERE k.lot_id = @Id
        """;

    private const string Content = """
        SELECT d.site_id AS siteid, d.file_name AS filename, d.content_type AS contenttype, c.content
        FROM core.lot_document d JOIN core.lot_document_content c ON c.document_id = d.id
        WHERE d.id = @Id
        """;

    public async Task<IReadOnlyList<LotAttributeDto>> ListAttributesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AttributeListRow>(new CommandDefinition(Attributes, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new LotAttributeDto
        {
            Id = r.Id,
            SiteId = r.SiteId,
            Code = r.Code,
            Name = r.Name,
            DataType = r.DataType,
            UomId = r.UomId,
            UomCode = r.UomCode,
            Choices = r.Choices,
            AppliesToAll = r.AppliesToAll,
            ClassIds = r.ClassIds,
            ClassCodes = r.ClassCodes,
            SortOrder = r.SortOrder,
            IsActive = r.IsActive,
            LotsWithValue = r.LotsWithValue,
            Version = r.Version,
        })];
    }

    public async Task<LotRecordContext?> GetLotContextAsync(Guid lotId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<LotRecordContext>(new CommandDefinition(LotRecordSql.LotContext, new { Id = lotId }, cancellationToken: cancellationToken));
    }

    public async Task<LotAttributeDefinition?> GetAttributeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<LotRecordSql.AttributeRow>(new CommandDefinition(Attribute, new { Id = id }, cancellationToken: cancellationToken)))?.ToDomain();
    }

    public async Task<IReadOnlyList<StoredAttributeValue>> ListValuesAsync(Guid lotId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<LotRecordSql.ValueRow>(new CommandDefinition(Values, new { Id = lotId }, cancellationToken: cancellationToken))).Select(r => r.ToStored())];
    }

    public async Task<IReadOnlyList<LinkedDocument>> ListDocumentsAsync(Guid lotId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<LotRecordSql.DocumentRow>(new CommandDefinition(Documents, new { Id = lotId }, cancellationToken: cancellationToken)))
            .Select(r => new LinkedDocument(r.ToDomain(), r.Via, r.FromLotId, r.FromLotNumber, r.Version))];
    }

    public async Task<LotDocumentContent?> GetContentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<LotDocumentContent>(new CommandDefinition(Content, new { Id = documentId }, cancellationToken: cancellationToken));
    }

    private sealed class AttributeListRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string DataType { get; init; } = "";
        public Guid? UomId { get; init; }
        public string? UomCode { get; init; }
        public string[] Choices { get; init; } = [];
        public bool AppliesToAll { get; init; }
        public Guid[] ClassIds { get; init; } = [];
        public string[] ClassCodes { get; init; } = [];
        public int SortOrder { get; init; }
        public bool IsActive { get; init; }
        public int LotsWithValue { get; init; }
        public long Version { get; init; }
    }
}
