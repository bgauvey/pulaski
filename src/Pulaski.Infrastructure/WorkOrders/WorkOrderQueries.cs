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
               w.created_at, w.updated_at, w.xmin::text::bigint AS version
        FROM core.work_order w
        JOIN core.equipment s ON s.id = w.site_id
        JOIN core.material m  ON m.id = w.material_id
        JOIN core.uom u       ON u.id = w.uom_id
        LEFT JOIN core.equipment t ON t.id = w.target_equipment_id
        LEFT JOIN core.process_version pv ON pv.id = w.process_version_id
        LEFT JOIN core.process p ON p.id = pv.process_id
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
}
