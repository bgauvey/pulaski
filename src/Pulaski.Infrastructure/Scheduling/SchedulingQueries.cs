using Dapper;
using Npgsql;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;

namespace Pulaski.Infrastructure.Scheduling;

/// <summary>Read side for scheduling: one connection per query, no locks.</summary>
internal sealed class SchedulingQueries(NpgsqlDataSource dataSource) : ISchedulingReadStore
{
    private const string RunStandards = """
        SELECT rs.id, rs.equipment_id, rs.material_id, m.code AS material_code, rs.uom_id, u.code AS uom_code,
               rs.calculated_rate_per_hour, rs.calculation_basis, rs.calculated_at,
               rs.modified_rate_per_hour, rs.modified_at, rs.modified_by, rs.modified_note,
               rs.rate_source, rs.effective_rate_per_hour, rs.xmin::text::bigint AS version
        FROM core.run_standard rs
        JOIN core.material m ON m.id = rs.material_id
        JOIN core.uom u      ON u.id = rs.uom_id
        WHERE rs.equipment_id = @LineId
        ORDER BY m.code
        """;

    // A site without a policy row gets the defaults, version 0.
    private const string Policy = """
        SELECT e.id AS site_id,
               coalesce(p.changeover_mode, 'auto') AS changeover_mode,
               p.site_id IS NULL AS is_default,
               coalesce(p.xmin::text::bigint, 0) AS version
        FROM core.equipment e
        LEFT JOIN core.scheduling_policy p ON p.site_id = e.id
        WHERE e.id = @SiteId
          AND e.level = 'site'
        """;

    public async Task<LinePlanSource?> GetLinePlanAsync(
        Guid lineId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await LinePlanLoader.LoadAsync(connection, null, lineId, windowStart, windowEnd, cancellationToken);
    }

    public async Task<IReadOnlyList<RunStandardDto>> ListRunStandardsAsync(Guid lineId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<RunStandardDto>(
            new CommandDefinition(RunStandards, new { LineId = lineId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<SchedulingPolicyDto?> GetPolicyAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<SchedulingPolicyDto>(
            new CommandDefinition(Policy, new { SiteId = siteId }, cancellationToken: cancellationToken));
    }
}
