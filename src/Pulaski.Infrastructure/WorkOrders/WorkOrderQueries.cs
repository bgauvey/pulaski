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
               w.created_at, w.updated_at, w.xmin::text::bigint AS version
        FROM core.work_order w
        JOIN core.equipment s ON s.id = w.site_id
        JOIN core.material m  ON m.id = w.material_id
        JOIN core.uom u       ON u.id = w.uom_id
        LEFT JOIN core.equipment t ON t.id = w.target_equipment_id
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
