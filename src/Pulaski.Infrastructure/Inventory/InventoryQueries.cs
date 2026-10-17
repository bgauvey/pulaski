using Dapper;
using Npgsql;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Read side for inventory (D-027). Quantities are in each material's base unit.</summary>
internal sealed class InventoryQueries(NpgsqlDataSource dataSource) : IInventoryReadStore
{
    private const string OnHand = "('available','on_hold','quarantine')";

    private const string StockSelect = $"""
        SELECT m.id AS material_id, m.code, m.name, m.material_type, m.material_class_id,
               c.code AS material_class_code, c.name AS material_class_name,
               m.base_uom_id, u.code AS base_uom_code, m.is_lot_tracked,
               coalesce(s.on_hand, 0) AS on_hand, coalesce(s.available, 0) AS available, coalesce(s.held, 0) AS held,
               s.lot_count, s.location_count, s.next_expiry
        FROM core.material m
        JOIN core.uom u ON u.id = m.base_uom_id
        LEFT JOIN core.material_class c ON c.id = m.material_class_id
        CROSS JOIN LATERAL (
            SELECT sum(l.quantity) AS on_hand,
                   sum(l.quantity) FILTER (WHERE l.status = 'available') AS available,
                   sum(l.quantity) FILTER (WHERE l.status <> 'available') AS held,
                   count(*)::int AS lot_count,
                   count(DISTINCT l.location_equipment_id)::int AS location_count,
                   min(l.expires_at) AS next_expiry
            FROM core.lot l
            WHERE l.material_id = m.id AND l.status IN {OnHand} AND l.quantity > 0
              AND (@SiteId::uuid IS NULL OR l.site_id = @SiteId)
              AND (@LocationId::uuid IS NULL OR l.location_equipment_id = @LocationId)) s
        """;

    private const string ListStockSql = $"""
        {StockSelect}
        WHERE m.is_active
          AND (@Pattern::text IS NULL OR m.code ILIKE @Pattern OR m.name ILIKE @Pattern)
          AND (@MaterialClassId::uuid IS NULL OR m.material_class_id = @MaterialClassId)
          AND (NOT @InStockOnly OR s.lot_count > 0)
        ORDER BY c.name NULLS LAST, m.code
        """;

    private const string MaterialStockSql = $"""
        {StockSelect}
        WHERE m.id = @MaterialId
        """;

    private const string LotColumns = """
        l.id, l.site_id, l.lot_number, l.is_bulk, l.material_id, m.code AS material_code, m.name AS material_name,
        l.status, l.quantity, l.uom_id, u.code AS uom_code,
        l.location_equipment_id AS location_id, loc.code AS location_code, loc.name AS location_name,
        l.origin, l.supplier_lot, l.expires_at, l.produced_by_job_id, pj.job_number AS produced_by_job_number,
        l.created_at, l.updated_at, l.xmin::text::bigint AS version
        """;

    private const string LotFrom = """
        FROM core.lot l
        JOIN core.material m ON m.id = l.material_id
        JOIN core.uom u ON u.id = l.uom_id
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        LEFT JOIN core.job pj ON pj.id = l.produced_by_job_id
        """;

    // Without a status, only lots with stock on hand.
    private const string SearchLotsSql = $"""
        SELECT {LotColumns}
        {LotFrom}
        WHERE (@SiteId::uuid IS NULL OR l.site_id = @SiteId)
          AND (@MaterialId::uuid IS NULL OR l.material_id = @MaterialId)
          AND (@LocationId::uuid IS NULL OR l.location_equipment_id = @LocationId)
          AND (@Pattern::text IS NULL OR l.lot_number ILIKE @Pattern OR l.supplier_lot ILIKE @Pattern OR m.code ILIKE @Pattern)
          AND (@ExpiresBefore::timestamptz IS NULL OR l.expires_at < @ExpiresBefore)
          AND (CASE WHEN @Status::text IS NULL THEN l.status IN {OnHand} AND l.quantity > 0 ELSE l.status = @Status END)
        ORDER BY m.code, l.lot_number
        LIMIT 500
        """;

    private const string MaterialLocationsSql = $"""
        SELECT loc.id AS location_id, loc.code AS location_code, loc.name AS location_name,
               count(*)::int AS lot_count, sum(l.quantity) AS on_hand,
               coalesce(sum(l.quantity) FILTER (WHERE l.status = 'available'), 0) AS available
        FROM core.lot l
        JOIN core.equipment loc ON loc.id = l.location_equipment_id
        WHERE l.material_id = @MaterialId AND l.status IN {OnHand} AND l.quantity > 0
          AND (@SiteId::uuid IS NULL OR l.site_id = @SiteId)
        GROUP BY loc.id, loc.code, loc.name
        ORDER BY loc.code
        """;

    private const string LotSql = $"""
        SELECT {LotColumns}
        {LotFrom}
        WHERE l.id = @Id
        """;

    private const string LotContextSql = """
        SELECT m.is_lot_tracked, s.time_zone
        FROM core.lot l
        JOIN core.material m ON m.id = l.material_id
        JOIN core.equipment s ON s.id = l.site_id
        WHERE l.id = @Id
        """;

    private const string HistorySql = """
        SELECT e.id, e.occurred_at, e.event_kind AS kind, e.qty_delta, e.qty_after, u.code AS uom_code,
               fl.code AS from_location_code, tl.code AS to_location_code, e.from_status, e.to_status,
               e.job_id, j.job_number, r.code AS reason_code, r.name AS reason_name, e.comment, e.actor_id,
               e.related_lot_id, rl.lot_number AS related_lot_number
        FROM ts.lot_event e
        LEFT JOIN core.uom u ON u.id = e.uom_id
        LEFT JOIN core.equipment fl ON fl.id = e.from_location_id
        LEFT JOIN core.equipment tl ON tl.id = e.to_location_id
        LEFT JOIN core.job j ON j.id = e.job_id
        LEFT JOIN core.reason_code r ON r.id = e.reason_code_id
        LEFT JOIN core.lot rl ON rl.id = e.related_lot_id
        WHERE e.lot_id = @Id
        ORDER BY e.occurred_at DESC, e.id DESC
        """;

    private const string LinkColumns = """
        o.id AS lot_id, o.lot_number, o.is_bulk, loc.code AS location_code, m.code AS material_code, m.name AS material_name,
        o.status, o.quantity, u.code AS uom_code, g.link_basis, j.job_number
        """;

    private const string LinkJoins = """
        JOIN core.material m ON m.id = o.material_id
        JOIN core.uom u ON u.id = o.uom_id
        LEFT JOIN core.equipment loc ON loc.id = o.location_equipment_id
        LEFT JOIN core.job j ON j.id = g.job_id
        """;

    private const string ParentsSql = $"""
        SELECT {LinkColumns}
        FROM core.lot_genealogy g
        JOIN core.lot o ON o.id = g.parent_lot_id
        {LinkJoins}
        WHERE g.child_lot_id = @Id AND g.superseded_at IS NULL
        ORDER BY m.code, o.lot_number
        """;

    private const string ChildrenSql = $"""
        SELECT {LinkColumns}
        FROM core.lot_genealogy g
        JOIN core.lot o ON o.id = g.child_lot_id
        {LinkJoins}
        WHERE g.parent_lot_id = @Id AND g.superseded_at IS NULL
        ORDER BY m.code, o.lot_number
        """;

    // A lot reached by several paths is kept once, at its nearest.
    private const string TraceSelect = """
        SELECT x.* FROM (
            SELECT DISTINCT ON (t.lot_id)
                   t.lot_id, t.via_lot_id, t.depth, l.lot_number, l.is_bulk, loc.code AS location_code,
                   m.code AS material_code, m.name AS material_name, l.status, l.quantity, u.code AS uom_code
        """;

    private const string TraceRest = """
            JOIN core.lot l ON l.id = t.lot_id
            JOIN core.material m ON m.id = l.material_id
            JOIN core.uom u ON u.id = l.uom_id
            LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
            ORDER BY t.lot_id, t.depth) x
        ORDER BY x.depth, x.material_code, x.lot_number
        """;

    private const string TraceForwardSql = $"""
        {TraceSelect}
            FROM core.trace_forward(@Id) t
        {TraceRest}
        """;

    private const string TraceBackwardSql = $"""
        {TraceSelect}
            FROM core.trace_backward(@Id) t
        {TraceRest}
        """;

    private const string BaseUomCodeSql = """
        SELECT u.code FROM core.material m JOIN core.uom u ON u.id = m.base_uom_id WHERE m.id = @MaterialId
        """;

    // Released items are rows; a draft's are in its definition (camelCase, as the BOM API writes it).
    private const string BomUsesSql = """
        SELECT b.id AS bom_id, v.id AS version_id, v.version_number, v.status AS version_status, b.is_active AS bom_is_active,
               pm.id AS material_id, pm.code AS material_code, pm.name AS material_name, p.code AS process_code,
               i.line_no, i.qty_per, u.code AS uom_code, v.base_qty, bu.code AS base_uom_code,
               i.operation_number, i.consumption_method
        FROM core.bom_item i
        JOIN core.bom_version v ON v.id = i.bom_version_id AND v.status = 'released'
        JOIN core.bom b ON b.id = v.bom_id
        JOIN core.material pm ON pm.id = b.material_id
        LEFT JOIN core.process p ON p.id = b.process_id
        JOIN core.uom u ON u.id = i.uom_id
        JOIN core.uom bu ON bu.id = v.base_uom_id
        WHERE i.component_material_id = @MaterialId
        UNION ALL
        SELECT b.id, v.id, v.version_number, v.status, b.is_active,
               pm.id, pm.code, pm.name, p.code,
               (it->>'lineNo')::int, (it->>'qtyPer')::numeric, u.code, v.base_qty, bu.code,
               (it->>'operationNumber')::int, it->>'consumptionMethod'
        FROM core.bom_version v
        CROSS JOIN LATERAL jsonb_array_elements(v.definition->'items') it
        JOIN core.bom b ON b.id = v.bom_id
        JOIN core.material pm ON pm.id = b.material_id
        LEFT JOIN core.process p ON p.id = b.process_id
        LEFT JOIN core.uom u ON u.id::text = it->>'uomId'
        JOIN core.uom bu ON bu.id = v.base_uom_id
        WHERE v.status = 'draft' AND it->>'componentMaterialId' = @MaterialId::text
        ORDER BY material_code, version_status DESC, line_no
        """;

    private const string OpenJobs = "j.job_type = 'production' AND j.status NOT IN ('completed','closed','cancelled')";

    private const string JobUsesSql = $"""
        SELECT j.id AS job_id, j.job_number, j.status AS job_status, wo.order_number AS work_order_number,
               eq.code AS line_code, om.code AS material_code, sum(jm.planned_qty) AS planned_qty, u.code AS uom_code,
               coalesce((SELECT sum(-e.qty_delta)
                         FROM ts.lot_event e JOIN core.lot l ON l.id = e.lot_id
                         WHERE e.job_id = j.id AND e.event_kind = 'consumed' AND l.material_id = @MaterialId), 0) AS consumed_qty
        FROM core.job_material jm
        JOIN core.job j ON j.id = jm.job_id
        JOIN core.equipment eq ON eq.id = j.equipment_id
        JOIN core.uom u ON u.id = jm.uom_id
        LEFT JOIN core.material om ON om.id = j.material_id
        LEFT JOIN core.work_order wo ON wo.id = j.work_order_id
        WHERE jm.material_id = @MaterialId AND jm.direction = 'consume' AND {OpenJobs}
        GROUP BY j.id, j.job_number, j.status, wo.order_number, eq.code, om.code, u.code
        ORDER BY j.job_number
        """;

    private const string LocationsSql = $"""
        SELECT e.id, e.site_id, e.code, e.name, e.equipment_role, p.code AS parent_code, e.is_active,
               coalesce(s.lot_count, 0) AS lot_count, coalesce(s.material_count, 0) AS material_count
        FROM core.equipment e
        LEFT JOIN core.equipment p ON p.id = e.parent_id
        LEFT JOIN LATERAL (
            SELECT count(*)::int AS lot_count, count(DISTINCT l.material_id)::int AS material_count
            FROM core.lot l
            WHERE l.location_equipment_id = e.id AND l.status IN {OnHand} AND l.quantity > 0) s ON true
        WHERE e.holds_inventory AND (@SiteId::uuid IS NULL OR e.site_id = @SiteId)
        ORDER BY e.code
        """;

    private const string JobColumns = """
        j.id, j.job_number, j.status, j.site_id, j.material_id, m.code AS material_code, m.name AS material_name,
        j.planned_qty, u.code AS uom_code, eq.code AS line_code, wo.order_number AS work_order_number
        """;

    private const string JobFrom = """
        FROM core.job j
        JOIN core.material m ON m.id = j.material_id
        JOIN core.equipment eq ON eq.id = j.equipment_id
        LEFT JOIN core.uom u ON u.id = j.uom_id
        LEFT JOIN core.work_order wo ON wo.id = j.work_order_id
        """;

    private const string DeclarableJobsSql = $"""
        SELECT {JobColumns},
               (@MaterialId::uuid IS NOT NULL AND EXISTS (
                   SELECT 1 FROM core.job_material jm
                   WHERE jm.job_id = j.id AND jm.material_id = @MaterialId AND jm.direction = 'consume')) AS plans_component
        {JobFrom}
        WHERE {OpenJobs}
          AND (@SiteId::uuid IS NULL OR j.site_id = @SiteId)
          AND (@Pattern::text IS NULL OR j.job_number ILIKE @Pattern OR m.code ILIKE @Pattern OR wo.order_number ILIKE @Pattern)
        ORDER BY plans_component DESC, j.job_number
        LIMIT 200
        """;

    private const string JobSql = $"""
        SELECT {JobColumns}, false AS plans_component
        {JobFrom}
        WHERE j.id = @Id AND j.job_type = 'production'
        """;

    private const string PlannedComponentsSql = """
        SELECT jm.material_id, m.code AS material_code, m.name AS material_name, sum(jm.planned_qty) AS planned_qty,
               u.code AS planned_uom_code, bu.code AS base_uom_code
        FROM core.job_material jm
        JOIN core.material m ON m.id = jm.material_id
        JOIN core.uom u ON u.id = jm.uom_id
        JOIN core.uom bu ON bu.id = m.base_uom_id
        WHERE jm.job_id = @Id AND jm.direction = 'consume'
        GROUP BY jm.material_id, m.code, m.name, u.code, bu.code
        ORDER BY m.code, u.code
        """;

    private const string ConsumedSql = """
        SELECT l.material_id, m.code AS material_code, m.name AS material_name, sum(-e.qty_delta) AS consumed_qty, bu.code AS base_uom_code
        FROM ts.lot_event e
        JOIN core.lot l ON l.id = e.lot_id
        JOIN core.material m ON m.id = l.material_id
        JOIN core.uom bu ON bu.id = m.base_uom_id
        WHERE e.job_id = @Id AND e.event_kind = 'consumed'
        GROUP BY l.material_id, m.code, m.name, bu.code
        ORDER BY m.code
        """;

    private const string ProducedLotsSql = $"""
        SELECT {LotColumns}
        {LotFrom}
        WHERE l.id IN (SELECT e.lot_id FROM ts.lot_event e WHERE e.job_id = @Id AND e.event_kind = 'produced')
        ORDER BY l.lot_number
        """;

    private const string ProducedQtySql = """
        SELECT coalesce(sum(qty_delta), 0) FROM ts.lot_event WHERE job_id = @Id AND event_kind = 'produced'
        """;

    private const string ReasonCodesSql = """
        SELECT id, reason_kind AS kind, code, name, requires_comment
        FROM core.reason_code
        WHERE is_active AND is_selectable AND reason_kind = @Kind
          AND (site_id IS NULL OR site_id = @SiteId)
        ORDER BY sort_order, name
        """;

    public async Task<IReadOnlyList<MaterialStockDto>> ListStockAsync(StockFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialStockDto>(new CommandDefinition(
            ListStockSql,
            new { filter.SiteId, filter.LocationId, filter.MaterialClassId, filter.InStockOnly, Pattern = Like(filter.Search) },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<MaterialStockDetailDto?> GetMaterialStockAsync(Guid materialId, Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { MaterialId = materialId, SiteId = siteId, LocationId = (Guid?)null };
        var material = await connection.QuerySingleOrDefaultAsync<MaterialStockDto>(
            new CommandDefinition(MaterialStockSql, args, cancellationToken: cancellationToken));
        if (material is null)
        {
            return null;
        }

        var lots = await connection.QueryAsync<LotSummaryDto>(
            new CommandDefinition(SearchLotsSql, LotArgs(new LotFilter(SiteId: siteId, MaterialId: materialId)), cancellationToken: cancellationToken));
        var locations = await connection.QueryAsync<LocationStockDto>(
            new CommandDefinition(MaterialLocationsSql, args, cancellationToken: cancellationToken));
        return new MaterialStockDetailDto { Material = material, Lots = lots.AsList(), Locations = locations.AsList() };
    }

    public async Task<IReadOnlyList<LotSummaryDto>> SearchLotsAsync(LotFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LotSummaryDto>(new CommandDefinition(SearchLotsSql, LotArgs(filter), cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<LotDto?> GetLotAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { Id = id };
        var lot = await connection.QuerySingleOrDefaultAsync<LotSummaryDto>(new CommandDefinition(LotSql, args, cancellationToken: cancellationToken));
        if (lot is null)
        {
            return null;
        }

        var context = await connection.QuerySingleAsync<LotContextRow>(new CommandDefinition(LotContextSql, args, cancellationToken: cancellationToken));
        var history = await connection.QueryAsync<LotEventDto>(new CommandDefinition(HistorySql, args, cancellationToken: cancellationToken));
        var parents = await connection.QueryAsync<LotLinkDto>(new CommandDefinition(ParentsSql, args, cancellationToken: cancellationToken));
        var children = await connection.QueryAsync<LotLinkDto>(new CommandDefinition(ChildrenSql, args, cancellationToken: cancellationToken));
        return new LotDto
        {
            Lot = lot,
            MaterialIsLotTracked = context.IsLotTracked,
            SiteTimeZone = context.TimeZone,
            History = history.AsList(),
            Parents = parents.AsList(),
            Children = children.AsList(),
        };
    }

    public async Task<IReadOnlyList<LotTraceNodeDto>> TraceAsync(Guid lotId, bool forward, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LotTraceNodeDto>(
            new CommandDefinition(forward ? TraceForwardSql : TraceBackwardSql, new { Id = lotId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<WhereUsedDto?> WhereUsedAsync(Guid materialId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { MaterialId = materialId };
        var baseUom = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(BaseUomCodeSql, args, cancellationToken: cancellationToken));
        if (baseUom is null)
        {
            return null;
        }

        var boms = await connection.QueryAsync<BomUseDto>(new CommandDefinition(BomUsesSql, args, cancellationToken: cancellationToken));
        var jobs = await connection.QueryAsync<JobUseDto>(new CommandDefinition(JobUsesSql, args, cancellationToken: cancellationToken));
        return new WhereUsedDto { MaterialId = materialId, BaseUomCode = baseUom, Boms = boms.AsList(), Jobs = jobs.AsList() };
    }

    public async Task<IReadOnlyList<InventoryLocationDto>> ListLocationsAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<InventoryLocationDto>(
            new CommandDefinition(LocationsSql, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<DeclarableJobDto>> ListDeclarableJobsAsync(
        Guid? siteId, string? search, Guid? materialId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<DeclarableJobDto>(new CommandDefinition(
            DeclarableJobsSql,
            new { SiteId = siteId, MaterialId = materialId, Pattern = Like(search) },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<JobMaterialsDto?> GetJobMaterialsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { Id = jobId };
        var job = await connection.QuerySingleOrDefaultAsync<DeclarableJobDto>(new CommandDefinition(JobSql, args, cancellationToken: cancellationToken));
        if (job is null)
        {
            return null;
        }

        var planned = (await connection.QueryAsync<ComponentRow>(new CommandDefinition(PlannedComponentsSql, args, cancellationToken: cancellationToken))).AsList();
        var consumed = (await connection.QueryAsync<ComponentRow>(new CommandDefinition(ConsumedSql, args, cancellationToken: cancellationToken))).AsList();
        var produced = await connection.QueryAsync<LotSummaryDto>(new CommandDefinition(ProducedLotsSql, args, cancellationToken: cancellationToken));
        var producedQty = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(ProducedQtySql, args, cancellationToken: cancellationToken));
        var producedUom = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(BaseUomCodeSql, new { job.MaterialId }, cancellationToken: cancellationToken));

        return new JobMaterialsDto
        {
            Job = job,
            Components = MergeComponents(planned, consumed),
            Produced = produced.AsList(),
            ProducedQty = producedQty,
            ProducedUomCode = producedUom,
        };
    }

    public async Task<IReadOnlyList<ReasonCodeDto>> ListReasonCodesAsync(string kind, Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReasonCodeDto>(
            new CommandDefinition(ReasonCodesSql, new { Kind = kind, SiteId = siteId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    /// <summary>
    /// The planned components with what was consumed of each (on the first row of its material),
    /// then anything consumed that wasn't planned. Both lists come sorted by material code.
    /// </summary>
    private static List<JobComponentUseDto> MergeComponents(List<ComponentRow> planned, List<ComponentRow> consumed)
    {
        var used = consumed.ToDictionary(c => c.MaterialId, c => c.ConsumedQty);
        var seen = new HashSet<Guid>();
        var result = planned.Select(p => new JobComponentUseDto
        {
            MaterialId = p.MaterialId,
            MaterialCode = p.MaterialCode,
            MaterialName = p.MaterialName,
            IsPlanned = true,
            PlannedQty = p.PlannedQty,
            PlannedUomCode = p.PlannedUomCode,
            ConsumedQty = seen.Add(p.MaterialId) ? used.GetValueOrDefault(p.MaterialId) : 0m,
            BaseUomCode = p.BaseUomCode,
        }).ToList();

        result.AddRange(consumed.Where(c => !seen.Contains(c.MaterialId)).Select(c => new JobComponentUseDto
        {
            MaterialId = c.MaterialId,
            MaterialCode = c.MaterialCode,
            MaterialName = c.MaterialName,
            IsPlanned = false,
            ConsumedQty = c.ConsumedQty,
            BaseUomCode = c.BaseUomCode,
        }));
        return result;
    }

    private static object LotArgs(LotFilter filter) => new
    {
        filter.SiteId,
        filter.MaterialId,
        filter.LocationId,
        filter.ExpiresBefore,
        Status = string.IsNullOrWhiteSpace(filter.Status) ? null : filter.Status,
        Pattern = Like(filter.Search),
    };

    /// <summary>A contains-pattern for ILIKE with the user's wildcards escaped; null for a blank search.</summary>
    internal static string? Like(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? null
            : "%" + search.Trim().Replace(@"\", @"\\", StringComparison.Ordinal)
                .Replace("%", @"\%", StringComparison.Ordinal)
                .Replace("_", @"\_", StringComparison.Ordinal) + "%";

    private sealed class LotContextRow
    {
        public bool IsLotTracked { get; init; }
        public string? TimeZone { get; init; }
    }

    private sealed class ComponentRow
    {
        public Guid MaterialId { get; init; }
        public string MaterialCode { get; init; } = "";
        public string MaterialName { get; init; } = "";
        public decimal PlannedQty { get; init; }
        public string? PlannedUomCode { get; init; }
        public decimal ConsumedQty { get; init; }
        public string BaseUomCode { get; init; } = "";
    }
}
