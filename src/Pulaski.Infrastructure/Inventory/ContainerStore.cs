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
        id, site_id, code, kind, location_equipment_id AS location_id, is_active, created_at, created_by, parent_container_id AS parent_id,
        mixes_lots, flow_model, min_residence, xmin::text::bigint AS version
        """;

    private const string LockMany = $"SELECT {Columns} FROM core.container WHERE id = ANY(@Ids) ORDER BY id FOR UPDATE";

    // Everything inside, at any depth; the depth limit (5) keeps a damaged chain from running on.
    private const string LockInside = """
        WITH RECURSIVE below AS (
            SELECT id, 1 AS depth FROM core.container WHERE parent_container_id = @Id AND is_active
            UNION ALL
            SELECT c.id, b.depth + 1 FROM core.container c JOIN below b ON c.parent_container_id = b.id WHERE c.is_active AND b.depth < 10)
        SELECT c.id, c.site_id, c.code, c.kind, c.location_equipment_id AS location_id, c.is_active, c.created_at, c.created_by,
               c.parent_container_id AS parent_id, c.mixes_lots, c.flow_model, c.min_residence, c.xmin::text::bigint AS version, b.depth
        FROM core.container c JOIN below b ON b.id = c.id
        ORDER BY c.id
        FOR UPDATE OF c
        """;

    private const string Ancestors = """
        WITH RECURSIVE up AS (
            SELECT parent_container_id AS id, 1 AS n FROM core.container WHERE id = @Id AND parent_container_id IS NOT NULL
            UNION ALL
            SELECT c.parent_container_id, up.n + 1 FROM core.container c JOIN up ON c.id = up.id WHERE c.parent_container_id IS NOT NULL AND up.n < 10)
        SELECT id FROM up ORDER BY n
        """;

    private const string LotIdsIn = """
        SELECT id FROM core.lot
        WHERE container_id = ANY(@Ids) AND quantity > 0 AND status IN ('available','on_hold','quarantine')
        ORDER BY id
        """;

    private const string GetById = $"SELECT {Columns} FROM core.container WHERE id = @Id FOR UPDATE";

    private const string CodeExists = "SELECT EXISTS (SELECT 1 FROM core.container WHERE site_id = @SiteId AND code = @Code)";

    private const string NextLabel = "SELECT nextval('core.label_seq')";

    private const string Insert = """
        INSERT INTO core.container (id, site_id, code, kind, location_equipment_id, label_no, sscc, is_active, created_at, created_by, updated_at,
                                    mixes_lots, flow_model, min_residence)
        VALUES (@Id, @SiteId, @Code, @Kind, @LocationId, @LabelNo, @Sscc, @IsActive, @CreatedAt, @CreatedBy, @At, @MixesLots, @FlowModel, @MinResidence)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.container SET location_equipment_id = @LocationId, is_active = @IsActive, parent_container_id = @ParentId,
            mixes_lots = @MixesLots, flow_model = @FlowModel, min_residence = @MinResidence, updated_at = @At
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
            ? Map(r)
            : null;

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CodeExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public Task<long> NextLabelNoAsync(CancellationToken cancellationToken) => session.ExecuteScalarAsync<long>(NextLabel, null, cancellationToken);

    public Task<long> InsertAsync(Container container, long labelNo, string? sscc, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            Insert,
            new
            {
                container.Id,
                container.SiteId,
                container.Code,
                container.Kind,
                container.LocationId,
                LabelNo = labelNo,
                Sscc = sscc,
                container.IsActive,
                container.CreatedAt,
                container.CreatedBy,
                At = at,
                container.MixesLots,
                container.FlowModel,
                container.MinResidence,
            },
            cancellationToken);

    public Task<long> UpdateAsync(Container container, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            Update,
            new { container.Id, container.LocationId, container.IsActive, container.ParentId, container.MixesLots, container.FlowModel, container.MinResidence, At = at },
            cancellationToken);

    public Task<IReadOnlyList<Guid>> ListLotIdsAsync(Guid containerId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(LotIds, new { Id = containerId }, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListLotIdsInAsync(IReadOnlyCollection<Guid> containerIds, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(LotIdsIn, new { Ids = containerIds.ToArray() }, cancellationToken);

    public async Task<IReadOnlyList<Versioned<Container>>> LockContainersAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<Row>(LockMany, new { Ids = ids.ToArray() }, cancellationToken)).Select(Map)];

    public async Task<IReadOnlyList<(Versioned<Container> Container, int Depth)>> LockInsideAsync(Guid id, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<Row>(LockInside, new { Id = id }, cancellationToken)).Select(r => (Map(r), r.Depth))];

    public Task<IReadOnlyList<Guid>> ListAncestorIdsAsync(Guid id, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(Ancestors, new { Id = id }, cancellationToken);

    private static Versioned<Container> Map(Row r) =>
        new(new Container(r.Id, r.SiteId, r.Code, r.Kind, r.LocationId, r.IsActive, r.CreatedAt, r.CreatedBy, r.ParentId, r.MixesLots, r.FlowModel, r.MinResidence), r.Version);

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
        public Guid? ParentId { get; init; }
        public bool MixesLots { get; init; }
        public string FlowModel { get; init; } = "mixed";
        public TimeSpan? MinResidence { get; init; }
        public long Version { get; init; }
        public int Depth { get; init; }
    }
}

/// <summary>Read side for containers and scans (D-059).</summary>
internal sealed class ContainerQueries(NpgsqlDataSource dataSource, IInventoryReadStore inventory) : IContainerReadStore
{
    private const string OnHand = "('available','on_hold','quarantine')";

    private const string ContainerSelect = $"""
        SELECT c.id, c.site_id, c.code, c.kind, c.location_equipment_id AS location_id, loc.code AS location_code, c.is_active, c.label_no,
               'PUL1C' || c.label_no AS barcode, c.sscc, c.parent_container_id, pc.code AS parent_container_code,
               c.mixes_lots, c.flow_model, (extract(epoch FROM c.min_residence) / 60)::int AS min_residence_minutes,
               (SELECT count(*)::int FROM core.lot l WHERE l.container_id = c.id AND l.quantity > 0 AND l.status IN {OnHand}) AS lot_count,
               (SELECT count(*)::int FROM core.container k WHERE k.parent_container_id = c.id AND k.is_active) AS container_count,
               c.created_at, c.created_by, c.xmin::text::bigint AS version
        FROM core.container c
        JOIN core.equipment loc ON loc.id = c.location_equipment_id
        LEFT JOIN core.container pc ON pc.id = c.parent_container_id
        """;

    private const string Inside = $"{ContainerSelect} WHERE c.parent_container_id = @Id AND c.is_active ORDER BY c.code";

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
        SELECT 'container', c.id, c.code, loc.code || coalesce(' · in ' || pc.code, '') || ' · ' ||
               (SELECT count(*) FROM core.lot l WHERE l.container_id = c.id AND l.quantity > 0 AND l.status IN {OnHand}) || ' lots',
               CASE WHEN @Kind = 'container' THEN 'label' ELSE 'container code' END
        FROM core.container c
        JOIN core.equipment loc ON loc.id = c.location_equipment_id
        LEFT JOIN core.container pc ON pc.id = c.parent_container_id
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
        var inside = await connection.QueryAsync<ContainerDto>(new CommandDefinition(Inside, new { Id = id }, cancellationToken: cancellationToken));
        return container with { Lots = lots, Containers = inside.AsList() };
    }

    public async Task<IReadOnlyList<ScanMatchDto>> FindAsync(Guid siteId, ScanCode code, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<ScanMatchDto>(new CommandDefinition(
            Find, new { SiteId = siteId, code.Kind, LabelNo = code.LabelNo ?? -1, Text = code.Text ?? "" }, cancellationToken: cancellationToken))];
    }
    // A GS1 or supplier code (#75): its material by GTIN, else by code.
    private const string ScannedMaterial = """
        SELECT id, code FROM core.material
        WHERE (@Gtin::text IS NOT NULL AND gtin = @Gtin) OR (@Code::text IS NOT NULL AND upper(code) = upper(@Code))
        ORDER BY (gtin = @Gtin::text) DESC NULLS LAST
        LIMIT 1
        """;

    // Its container by SSCC, or its lots on hand by lot or supplier lot number, of its material when it is known.
    private const string ScannedThings = $"""
        SELECT 'container' AS kind, c.id, c.code AS label,
               loc.code || ' · ' || (SELECT count(*) FROM core.lot l WHERE l.container_id = c.id AND l.quantity > 0 AND l.status IN {OnHand}) || ' lots' AS detail,
               'SSCC' AS matched_by
        FROM core.container c
        JOIN core.equipment loc ON loc.id = c.location_equipment_id
        WHERE c.site_id = @SiteId AND c.is_active AND @Sscc::text IS NOT NULL AND c.sscc = @Sscc
        UNION ALL
        SELECT 'lot', l.id, coalesce(l.lot_number, loc.code),
               m.code || ' · ' || trim(to_char(l.quantity, 'FM999999999990.######')) || ' ' || u.code || coalesce(' · ' || loc.code, '')
                   || coalesce(' · in ' || ct.code, ''),
               CASE WHEN lower(l.lot_number) = lower(@Lot) THEN 'lot number' ELSE 'supplier lot' END
        FROM core.lot l
        JOIN core.material m ON m.id = l.material_id
        JOIN core.uom u ON u.id = l.uom_id
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        LEFT JOIN core.container ct ON ct.id = l.container_id
        WHERE l.site_id = @SiteId AND l.status IN {OnHand} AND @Sscc::text IS NULL
          AND (@MaterialId::uuid IS NULL OR l.material_id = @MaterialId)
          AND (lower(l.lot_number) = lower(@Lot) OR lower(l.supplier_lot) = lower(@Lot) OR lower(l.supplier_lot) = lower(@SupplierLot))
        ORDER BY 1, 3
        LIMIT 20
        """;

    public async Task<(IReadOnlyList<ScanMatchDto> Matches, Guid? MaterialId, string? MaterialCode)> FindScannedAsync(
        Guid siteId, ScannedLot read, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var material = read.Gtin is null && read.MaterialCode is null
            ? null
            : await connection.QuerySingleOrDefaultAsync<MaterialRow>(new CommandDefinition(
                ScannedMaterial, new { read.Gtin, Code = read.MaterialCode }, cancellationToken: cancellationToken));
        var matches = read.Sscc is null && read.LotNumber is null && read.SupplierLot is null
            ? []
            : (await connection.QueryAsync<ScanMatchDto>(new CommandDefinition(
                ScannedThings,
                new { SiteId = siteId, read.Sscc, MaterialId = material?.Id, Lot = read.LotNumber ?? read.SupplierLot, read.SupplierLot },
                cancellationToken: cancellationToken))).AsList();
        return (matches, material?.Id, material?.Code);
    }

    private sealed class MaterialRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
    }
}
