using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Expected receipts (D-058), written in the current transaction.</summary>
internal sealed class ExpectedReceiptRepository(DbSession session) : IExpectedReceiptRepository
{
    private const string Columns = """
        id, site_id, material_id, quantity, uom_id, expected_at, reference, supplier, note, status, received_lot_id,
        external_system, external_key, created_at, created_by, xmin::text::bigint AS version
        """;

    private const string GetById = $"SELECT {Columns} FROM core.expected_receipt WHERE id = @Id FOR UPDATE";

    private const string GetByKey = $"SELECT {Columns} FROM core.expected_receipt WHERE external_system = @System AND external_key = @Key FOR UPDATE";

    private const string Insert = """
        INSERT INTO core.expected_receipt (id, site_id, material_id, quantity, uom_id, expected_at, reference, supplier, note, status,
                                           received_lot_id, external_system, external_key, created_at, created_by, updated_at)
        VALUES (@Id, @SiteId, @MaterialId, @Quantity, @UomId, @ExpectedAt, @Reference, @Supplier, @Note, @Status,
                @ReceivedLotId, @ExternalSystem, @ExternalKey, @CreatedAt, @CreatedBy, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.expected_receipt
        SET quantity = @Quantity, uom_id = @UomId, expected_at = @ExpectedAt, reference = @Reference, supplier = @Supplier, note = @Note,
            status = @Status, received_lot_id = @ReceivedLotId, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    public async Task<Versioned<ExpectedReceipt>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<Row>(GetById, new { Id = id }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<ExpectedReceipt>?> FindByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<Row>(GetByKey, new { System = externalSystem, Key = externalKey }, cancellationToken))?.ToVersioned();

    public Task<long> InsertAsync(ExpectedReceipt receipt, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Args(receipt, at), cancellationToken);

    public Task<long> UpdateAsync(ExpectedReceipt receipt, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, Args(receipt, at), cancellationToken);

    private static object Args(ExpectedReceipt r, DateTimeOffset at) => new
    {
        r.Id,
        r.SiteId,
        r.MaterialId,
        r.Quantity,
        r.UomId,
        r.ExpectedAt,
        r.Reference,
        r.Supplier,
        r.Note,
        r.Status,
        r.ReceivedLotId,
        r.ExternalSystem,
        r.ExternalKey,
        r.CreatedAt,
        r.CreatedBy,
        At = at,
    };

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid MaterialId { get; init; }
        public decimal Quantity { get; init; }
        public Guid UomId { get; init; }
        public DateTimeOffset ExpectedAt { get; init; }
        public string? Reference { get; init; }
        public string? Supplier { get; init; }
        public string? Note { get; init; }
        public string Status { get; init; } = "";
        public Guid? ReceivedLotId { get; init; }
        public string? ExternalSystem { get; init; }
        public string? ExternalKey { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string CreatedBy { get; init; } = "";
        public long Version { get; init; }

        public Versioned<ExpectedReceipt> ToVersioned() => new(
            new ExpectedReceipt(Id, SiteId, MaterialId, Quantity, UomId, ExpectedAt, Reference, Supplier, Note, Status, ReceivedLotId,
                ExternalSystem, ExternalKey, CreatedAt, CreatedBy),
            Version);
    }
}

/// <summary>Read side for expected receipts (D-058).</summary>
internal sealed class ExpectedReceiptQueries(NpgsqlDataSource dataSource) : IExpectedReceiptReadStore
{
    private const string List = """
        SELECT r.id, r.site_id, r.material_id, m.code AS material_code, m.name AS material_name, r.quantity, r.uom_id, u.code AS uom_code,
               r.expected_at, r.reference, r.supplier, r.note, r.status, r.received_lot_id, l.lot_number AS received_lot_number,
               r.external_system, r.external_key, r.created_at, r.created_by, r.xmin::text::bigint AS version
        FROM core.expected_receipt r
        JOIN core.material m ON m.id = r.material_id
        JOIN core.uom u ON u.id = r.uom_id
        LEFT JOIN core.lot l ON l.id = r.received_lot_id
        WHERE r.site_id = @SiteId AND (@IncludeClosed OR r.status = 'open') AND (@MaterialId::uuid IS NULL OR r.material_id = @MaterialId)
        ORDER BY r.status = 'open' DESC, r.expected_at, m.code
        LIMIT 2000
        """;

    public async Task<IReadOnlyList<ExpectedReceiptDto>> ListAsync(Guid siteId, bool includeClosed, Guid? materialId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<ExpectedReceiptDto>(new CommandDefinition(
            List, new { SiteId = siteId, IncludeClosed = includeClosed, MaterialId = materialId }, cancellationToken: cancellationToken))];
    }
}
