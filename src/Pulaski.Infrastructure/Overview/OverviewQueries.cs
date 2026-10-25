using Dapper;
using Npgsql;
using Pulaski.Application.Overview;

namespace Pulaski.Infrastructure.Overview;

/// <summary>Read side for the Overview (D-070): its lines and held lots; machine history comes from the OEE report's store.</summary>
internal sealed class OverviewQueries(NpgsqlDataSource dataSource) : IOverviewReadStore
{
    private const string Lines = """
        SELECT id, code FROM core.equipment
        WHERE site_id = @SiteId AND is_active AND can_run_job
        ORDER BY code
        """;

    private const string HeldLots = """
        SELECT count(*) FILTER (WHERE status = 'on_hold')::int AS on_hold,
               count(*) FILTER (WHERE status = 'quarantine')::int AS quarantine
        FROM core.lot
        WHERE site_id = @SiteId AND status IN ('on_hold','quarantine') AND quantity > 0
        """;

    public async Task<IReadOnlyList<OverviewLineRecord>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<OverviewLineRecord>(new CommandDefinition(Lines, new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }

    public async Task<(int OnHold, int Quarantine)> CountHeldLotsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<HeldRow>(new CommandDefinition(HeldLots, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return (row.OnHold, row.Quarantine);
    }

    private sealed record HeldRow(int OnHold, int Quarantine);
}
