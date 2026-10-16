using Dapper;
using Npgsql;
using Pulaski.Application.EquipmentModel;
using Pulaski.Contracts.EquipmentModel;

namespace Pulaski.Infrastructure.EquipmentModel;

/// <summary>Read side for equipment: parameterized SELECTs mapped straight to DTOs.</summary>
internal sealed class EquipmentQueries(NpgsqlDataSource dataSource) : IEquipmentReadStore
{
    // Tree order: each row follows its parent, siblings by code.
    private const string List = """
        WITH RECURSIVE tree AS (
            SELECT e.*, e.xmin::text::bigint AS version, 0 AS depth, ARRAY[e.code] AS path
            FROM core.equipment e
            WHERE e.parent_id IS NULL
            UNION ALL
            SELECT c.*, c.xmin::text::bigint, t.depth + 1, t.path || c.code
            FROM core.equipment c
            JOIN tree t ON c.parent_id = t.id
        )
        SELECT id, parent_id, site_id, code, name, description, level, equipment_role,
               tracks_state, is_schedulable, can_run_job, holds_inventory, time_zone, is_active, depth, version
        FROM tree
        WHERE @SiteId::uuid IS NULL OR site_id = @SiteId OR level = 'enterprise'
        ORDER BY path
        """;

    private const string Get = """
        SELECT e.id, e.parent_id, e.site_id, e.code, e.name, e.description, e.level, e.equipment_role,
               e.tracks_state, e.is_schedulable, e.can_run_job, e.holds_inventory, e.time_zone, e.is_active,
               (SELECT max(c.depth) FROM core.equipment_closure c WHERE c.descendant_id = e.id) AS depth,
               e.xmin::text::bigint AS version
        FROM core.equipment e
        WHERE e.id = @Id
        """;

    public async Task<IReadOnlyList<EquipmentDto>> ListAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EquipmentDto>(
            new CommandDefinition(List, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<EquipmentDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<EquipmentDto>(
            new CommandDefinition(Get, new { Id = id }, cancellationToken: cancellationToken));
    }
}
