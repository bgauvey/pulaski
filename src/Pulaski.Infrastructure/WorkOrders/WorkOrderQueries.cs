using Dapper;
using Npgsql;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Infrastructure.WorkOrders;

/// <summary>Read side for work orders: parameterized SELECTs mapped straight to DTOs.</summary>
internal sealed class WorkOrderQueries(NpgsqlDataSource dataSource) : IWorkOrderReadStore
{
    private const string Select = """
        SELECT w.id, w.site_id, s.code AS site_code, w.order_number, w.material_id,
               m.code AS material_code, m.name AS material_name, w.planned_qty, w.uom_id,
               u.code AS uom_code, w.target_equipment_id, t.code AS target_equipment_code,
               w.planned_start, w.planned_end, w.due_at, w.priority, w.status, w.origin,
               w.process_version_id, pv.process_id, p.code AS process_code, p.name AS process_name,
               pv.version_number AS process_version_number, w.notes,
               w.bom_version_id, bv.version_number AS bom_version_number,
               w.created_at, w.updated_at, w.xmin::text::bigint AS version
        FROM core.work_order w
        JOIN core.equipment s ON s.id = w.site_id
        JOIN core.material m  ON m.id = w.material_id
        JOIN core.uom u       ON u.id = w.uom_id
        LEFT JOIN core.equipment t ON t.id = w.target_equipment_id
        LEFT JOIN core.process_version pv ON pv.id = w.process_version_id
        LEFT JOIN core.process p ON p.id = pv.process_id
        LEFT JOIN core.bom_version bv ON bv.id = w.bom_version_id
        """;

    private const string List = $"""
        {Select}
        WHERE (@SiteId::uuid IS NULL OR w.site_id = @SiteId)
          AND (@Status::text IS NULL OR w.status = @Status)
        ORDER BY w.due_at NULLS LAST, w.priority DESC, w.order_number
        """;

    private const string Get = $"""
        {Select}
        WHERE w.id = @Id
        """;

    private const string ShopOrderSite = """
        SELECT time_zone
        FROM core.equipment
        WHERE id = (SELECT site_id FROM core.work_order WHERE id = @WorkOrderId)
        """;

    private const string ShopOrderJobs = """
        SELECT j.id AS job_id, j.job_number, j.job_type, j.status, j.equipment_id, e.code AS equipment_code,
               e.is_schedulable AND NOT e.can_run_job AS is_group,
               j.planned_qty, job_u.code AS uom_code, j.material_id, j.uom_id,
               o.operation_number, o.name AS operation_name, o.description AS operation_description,
               j.planned_start AS baseline_start, j.planned_end AS baseline_end,
               rs.effective_rate_per_hour AS rate_per_hour, rate_u.code AS rate_uom_code, rs.rate_source
        FROM core.job j
        JOIN core.equipment e ON e.id = j.equipment_id
        LEFT JOIN core.uom job_u ON job_u.id = j.uom_id
        LEFT JOIN core.process_operation o ON o.id = j.process_operation_id
        LEFT JOIN core.run_standard rs ON rs.equipment_id = j.equipment_id AND rs.material_id = j.material_id
        LEFT JOIN core.uom rate_u ON rate_u.id = rs.uom_id
        WHERE j.work_order_id = @WorkOrderId AND j.status <> 'cancelled'
        ORDER BY o.operation_number NULLS FIRST, j.job_number
        """;

    // The subtree, self included; equipment that holds jobs is an active line or a group above lines (D-022).
    private const string Scope = """
        SELECT e.id, e.site_id, (e.is_schedulable OR e.can_run_job) AND e.is_active AS holds_jobs
        FROM core.equipment e
        WHERE @EquipmentId::uuid IS NULL
           OR e.id IN (SELECT c.descendant_id FROM core.equipment_closure c WHERE c.ancestor_id = @EquipmentId)
        ORDER BY e.code
        """;

    private const string Facts = """
        SELECT w.id AS work_order_id,
               coalesce((SELECT sum(r.good_qty)
                         FROM core.job j
                         JOIN core.job_run r ON r.job_id = j.id
                         WHERE j.work_order_id = w.id), 0) AS completed_qty,
               (SELECT count(*)::int
                FROM core.job j
                WHERE j.work_order_id = w.id
                  AND j.status IN ('planned','dispatched','firmed','running','paused')) AS open_jobs
        FROM core.work_order w
        WHERE @SiteId::uuid IS NULL OR w.site_id = @SiteId
        """;

    public async Task<IReadOnlyList<ScopeEquipmentRow>> GetScopeAsync(Guid? equipmentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ScopeEquipmentRow>(
            new CommandDefinition(Scope, new { EquipmentId = equipmentId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<WorkOrderFactsRow>> GetFactsAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WorkOrderFactsRow>(
            new CommandDefinition(Facts, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<WorkOrderDto>> ListAsync(Guid? siteId, string? status, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WorkOrderDto>(
            new CommandDefinition(List, new { SiteId = siteId, Status = status }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<WorkOrderDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<WorkOrderDto>(
            new CommandDefinition(Get, new { Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<ShopOrderJobsRow?> GetShopOrderJobsAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var parameters = new { WorkOrderId = workOrderId };
        var siteTimeZone = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(ShopOrderSite, parameters, cancellationToken: cancellationToken));
        if (siteTimeZone is null)
        {
            return null;
        }

        var jobs = await connection.QueryAsync<ShopOrderJobRow>(
            new CommandDefinition(ShopOrderJobs, parameters, cancellationToken: cancellationToken));
        return new ShopOrderJobsRow
        {
            SiteTimeZone = siteTimeZone,
            Jobs = jobs.AsList(),
        };
    }
}
