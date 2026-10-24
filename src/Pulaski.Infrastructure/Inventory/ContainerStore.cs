using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Containers (D-059), written in the current transaction.</summary>
internal sealed class ContainerRepository(DbSession session) : IContainerRepository
{
    private const string Columns = """
        id, site_id, code, kind, location_equipment_id AS location_id, is_active, created_at, created_by, xmin::text::bigint AS version
        """;

    private const string GetById = $"SELECT {Columns} FROM core.container WHERE id = @Id FOR UPDATE";

    private const string CodeExists = "SELECT EXISTS (SELECT 1 FROM core.container WHERE site_id = @SiteId AND code = @Code)";

    private const string NextLabel = "SELECT nextval('core.label_seq')";

    private const string Insert = """
        INSERT INTO core.container (id, site_id, code, kind, location_equipment_id, label_no, is_active, created_at, created_by, updated_at)
        VALUES (@Id, @SiteId, @Code, @Kind, @LocationId, @LabelNo, @IsActive, @CreatedAt, @CreatedBy, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.container SET location_equipment_id = @LocationId, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string LotIds = """
        SELECT id FROM core.lot
        WHERE container_id = @Id AND quantity > 0 AND status IN ('available','on_hold','quarantine')
        ORDER BY id
        """;

    public async Task<Versioned<Container>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Row>(GetById, new { Id = id }, cancellationToken) is { } r
            ? new Versioned<Container>(new Container(r.Id, r.SiteId, r.Code, r.Kind, r.LocationId, r.IsActive, r.CreatedAt, r.CreatedBy), r.Version)
            : null;

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CodeExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public Task<long> NextLabelNoAsync(CancellationToken cancellationToken) => session.ExecuteScalarAsync<long>(NextLabel, null, cancellationToken);

    public Task<long> InsertAsync(Container container, long labelNo, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            Insert,
            new { container.Id, container.SiteId, container.Code, container.Kind, container.LocationId, LabelNo = labelNo, container.IsActive, container.CreatedAt, container.CreatedBy, At = at },
            cancellationToken);

    public Task<long> UpdateAsync(Container container, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, new { container.Id, container.LocationId, container.IsActive, At = at }, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListLotIdsAsync(Guid containerId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(LotIds, new { Id = containerId }, cancellationToken);

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Kind { get; init; } = "";
        public Guid LocationId { get; init; }
        public bool IsActive { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string CreatedBy { get; init; } = "";
        public long Version { get; init; }
    }
}

/// <summary>Read side for containers and scans (D-059).</summary>
internal sealed class ContainerQueries(NpgsqlDataSource dataSource, IInventoryReadStore inventory) : IContainerReadStore
{
    private const string OnHand = "('available','on_hold','quarantine')";

    private const string ContainerSelect = $"""
        SELECT c.id, c.site_id, c.code, c.kind, c.location_equipment_id AS location_id, loc.code AS location_code, c.is_active, c.label_no,
               'PUL1C' || c.label_no AS barcode,
               (SELECT count(*)::int FROM core.lot l WHERE l.container_id = c.id AND l.quantity > 0 AND l.status IN {OnHand}) AS lot_count,
               c.created_at, c.created_by, c.xmin::text::bigint AS version
        FROM core.container c
        JOIN core.equipment loc ON loc.id = c.location_equipment_id
        """;

    private const string List = $"""
        {ContainerSelect}
        WHERE c.site_id = @SiteId AND (@IncludeRetired OR c.is_active) AND (@Pattern::text IS NULL OR c.code ILIKE @Pattern OR loc.code ILIKE @Pattern)
        ORDER BY c.is_active DESC, c.code
        LIMIT 500
        """;

    private const string ById = $"{ContainerSelect} WHERE c.id = @Id";

    // A scan names lots and containers by label number, locations by code, and as free text anything whose number or code it is.
    private const string Find = $"""
        SELECT 'lot' AS kind, l.id, coalesce(l.lot_number, loc.code) AS label,
               m.code || ' · ' || trim(to_char(l.quantity, 'FM999999999990.######')) || ' ' || u.code || coalesce(' · ' || loc.code, '')
                   || coalesce(' · in ' || ct.code, '') AS detail,
               CASE WHEN @Kind = 'lot' THEN 'label' WHEN lower(l.lot_number) = lower(@Text) THEN 'lot number' ELSE 'supplier lot' END AS matched_by
        FROM core.lot l
        JOIN core.material m ON m.id = l.material_id
        JOIN core.uom u ON u.id = l.uom_id
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        LEFT JOIN core.container ct ON ct.id = l.container_id
        WHERE l.site_id = @SiteId
          AND ((@Kind = 'lot' AND l.label_no = @LabelNo)
            OR (@Kind = 'text' AND l.status IN {OnHand} AND (lower(l.lot_number) = lower(@Text) OR lower(l.supplier_lot) = lower(@Text))))
        UNION ALL
        SELECT 'container', c.id, c.code, loc.code || ' · ' ||
               (SELECT count(*) FROM core.lot l WHERE l.container_id = c.id AND l.quantity > 0 AND l.status IN {OnHand}) || ' lots',
               CASE WHEN @Kind = 'container' THEN 'label' ELSE 'container code' END
        FROM core.container c
        JOIN core.equipment loc ON loc.id = c.location_equipment_id
        WHERE c.site_id = @SiteId AND c.is_active
          AND ((@Kind = 'container' AND c.label_no = @LabelNo) OR (@Kind = 'text' AND upper(c.code) = upper(@Text)))
        UNION ALL
        SELECT 'location', e.id, e.code, e.name, CASE WHEN @Kind = 'location' THEN 'label' ELSE 'location code' END
        FROM core.equipment e
        WHERE e.site_id = @SiteId AND e.holds_inventory AND e.is_active
          AND (@Kind IN ('location','text') AND upper(e.code) = upper(@Text))
        ORDER BY 1, 3
        LIMIT 20
        """;

    public async Task<IReadOnlyList<ContainerDto>> ListAsync(Guid siteId, string? search, bool includeRetired, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<ContainerDto>(new CommandDefinition(
            List, new { SiteId = siteId, IncludeRetired = includeRetired, Pattern = InventoryQueries.Like(search) }, cancellationToken: cancellationToken))];
    }

    public async Task<ContainerDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (await connection.QuerySingleOrDefaultAsync<ContainerDto>(new CommandDefinition(ById, new { Id = id }, cancellationToken: cancellationToken)) is not { } container)
        {
            return null;
        }

        var lots = await inventory.SearchLotsAsync(new LotFilter(SiteId: container.SiteId, ContainerId: id), cancellationToken);
        return container with { Lots = lots };
    }

    public async Task<IReadOnlyList<ScanMatchDto>> FindAsync(Guid siteId, ScanCode code, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<ScanMatchDto>(new CommandDefinition(
            Find, new { SiteId = siteId, code.Kind, LabelNo = code.LabelNo ?? -1, Text = code.Text ?? "" }, cancellationToken: cancellationToken))];
    }
}
