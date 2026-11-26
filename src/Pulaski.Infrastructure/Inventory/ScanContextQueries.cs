using Dapper;
using Npgsql;
using Pulaski.Application.Inventory;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Reads for scanning at a line (#135).</summary>
internal sealed class ScanContextQueries(NpgsqlDataSource dataSource) : IScanContextReadStore
{
    public async Task<ScanLine?> GetLineAsync(Guid lineId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var line = await connection.QuerySingleOrDefaultAsync<LineRow>(new CommandDefinition(
            """
            SELECT e.id, e.code, e.site_id, e.holds_inventory, j.id AS job_id, j.job_number, r.id AS run_id
            FROM core.equipment e
            LEFT JOIN LATERAL (SELECT r.id, r.job_id FROM core.job_run r WHERE r.equipment_id = e.id AND r.ended_at IS NULL ORDER BY r.started_at DESC LIMIT 1) r ON true
            LEFT JOIN core.job j ON j.id = r.job_id
            WHERE e.id = @Id AND e.is_active
            """, new { Id = lineId }, cancellationToken: cancellationToken));
        if (line is null)
        {
            return null;
        }

        var feeds = await connection.QueryAsync<(Guid, string)>(new CommandDefinition(
            """
            SELECT f.id, f.code
            FROM core.equipment_closure c JOIN core.equipment f ON f.id = c.descendant_id
            WHERE c.ancestor_id = @Id AND c.depth > 0 AND f.equipment_role = 'feed_point' AND f.holds_inventory AND f.is_active
            ORDER BY f.code
            """, new { Id = lineId }, cancellationToken: cancellationToken));
        return new ScanLine(line.Id, line.Code, line.SiteId, line.HoldsInventory, line.JobId, line.JobNumber, line.RunId, [.. feeds]);
    }

    public async Task<IReadOnlyList<Pulaski.Contracts.Inventory.ScanLineOptionDto>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<Pulaski.Contracts.Inventory.ScanLineOptionDto>(new CommandDefinition(
            "SELECT id, code, name FROM core.equipment WHERE site_id = @SiteId AND can_run_job AND is_active ORDER BY code",
            new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }

    public async Task<ScanLot?> GetLotAsync(Guid lotId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ScanLot>(new CommandDefinition(
            """
            SELECT l.id, COALESCE(l.lot_number, '@' || COALESCE(loc.code, '?')) AS label, l.material_id, m.code AS material_code, l.status,
                   l.quantity, l.uom_id, u.code AS uom_code, l.location_equipment_id AS location_id, l.is_bulk, l.xmin::text::bigint AS version
            FROM core.lot l
            JOIN core.material m ON m.id = l.material_id
            JOIN core.uom u ON u.id = l.uom_id
            LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
            WHERE l.id = @Id
            """, new { Id = lotId }, cancellationToken: cancellationToken));
    }

    public async Task<ScanContainer?> GetContainerAsync(Guid containerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ScanContainer>(new CommandDefinition(
            "SELECT id, code, location_equipment_id AS location_id, xmin::text::bigint AS version FROM core.container WHERE id = @Id AND is_active",
            new { Id = containerId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ScanComponent>> GetComponentsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Planned in one unit per material (the first, when a BOM gave it twice); consumed in the material's base unit, reversals left out.
        return [.. await connection.QueryAsync<ScanComponent>(new CommandDefinition(
            $"""
            WITH planned AS (
                SELECT jm.material_id, sum(jm.planned_qty) AS planned_qty, min(jm.uom_id::text)::uuid AS planned_uom_id, count(DISTINCT jm.uom_id) AS units
                FROM core.job_material jm WHERE jm.job_id = @Id AND jm.direction = 'consume'
                GROUP BY jm.material_id
            ), consumed AS (
                SELECT l.material_id, sum(-e.qty_delta) AS qty
                FROM ts.lot_event e JOIN core.lot l ON l.id = e.lot_id
                WHERE e.job_id = @Id AND e.event_kind = 'consumed' AND {LotEventSql.NotReversed}
                GROUP BY l.material_id
            )
            SELECT COALESCE(p.material_id, c.material_id) AS material_id,
                   CASE WHEN p.units = 1 THEN p.planned_qty END AS planned_qty, CASE WHEN p.units = 1 THEN p.planned_uom_id END AS planned_uom_id,
                   COALESCE(c.qty, 0) AS consumed_base_qty
            FROM planned p FULL JOIN consumed c ON c.material_id = p.material_id
            WHERE p.material_id IS NOT NULL
            """, new { Id = jobId }, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<ScanJob>> FindJobsAsync(Guid siteId, string orderNumber, int? operationNumber, Guid lineId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<ScanJob>(new CommandDefinition(
            """
            SELECT j.id, j.job_number AS number, j.status, j.xmin::text::bigint AS version, j.equipment_id,
                   j.equipment_id = @LineId AS on_line,
                   EXISTS (SELECT 1 FROM core.equipment_closure c WHERE c.ancestor_id = j.equipment_id AND c.descendant_id = @LineId AND c.depth > 0) AS on_group_above
            FROM core.job j
            JOIN core.work_order w ON w.id = j.work_order_id
            LEFT JOIN core.process_operation o ON o.id = j.process_operation_id
            WHERE w.site_id = @SiteId AND upper(w.order_number) = upper(@OrderNumber)
              AND ((@Operation::int IS NULL AND o.operation_number IS NULL) OR o.operation_number = @Operation)
            ORDER BY j.job_number
            """, new { SiteId = siteId, OrderNumber = orderNumber, Operation = operationNumber, LineId = lineId }, cancellationToken: cancellationToken))];
    }

    private sealed class LineRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public Guid SiteId { get; init; }
        public bool HoldsInventory { get; init; }
        public Guid? JobId { get; init; }
        public string? JobNumber { get; init; }
        public Guid? RunId { get; init; }
    }
}
