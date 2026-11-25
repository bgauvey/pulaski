using Dapper;
using Npgsql;
using Pulaski.Application.Health;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Health;

/// <summary>The health page's reads (#141, D-118), each a small query over what Pulaski already keeps.</summary>
internal sealed class SystemHealthQueries(NpgsqlDataSource dataSource) : ISystemHealthReadStore
{
    public async Task<(string? Applied, string Required, bool HasRequired)> MigrationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var (applied, has) = await connection.QuerySingleAsync<(string?, bool)>(new CommandDefinition(
            """
            SELECT (SELECT max(version) FROM public.schema_migration),
                   EXISTS (SELECT 1 FROM public.schema_migration WHERE version = @Required)
            """, new { Required = DatabaseMaintenance.RequiredMigration }, cancellationToken: cancellationToken));
        return (applied, DatabaseMaintenance.RequiredMigration, has);
    }

    // Partitions are named <table>_pYYYYMM by ts.create_month_partitions.
    public async Task<IReadOnlyList<string>> MissingPartitionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT n.nspname || '.' || c.relname
            FROM pg_partitioned_table pt
            JOIN pg_class c ON c.oid = pt.partrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('ts', 'evt') AND pt.partstrat = 'r'
              AND NOT EXISTS (
                  SELECT 1 FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhrelid
                  WHERE i.inhparent = c.oid
                    AND p.relname = c.relname || '_p' || to_char(date_trunc('month', now()) + interval '1 month', 'YYYYMM'))
            ORDER BY 1
            """, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<(Guid Id, string Code, string SiteCode, bool IsActive, DateTimeOffset? LastSeenAt)>> EdgeNodesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<(Guid, string, string, bool, DateTimeOffset?)>(new CommandDefinition(
            """
            SELECT n.id, n.code, s.code, n.is_active, n.last_seen_at
            FROM core.edge_node n JOIN core.equipment s ON s.id = n.site_id
            ORDER BY s.code, n.code
            """, cancellationToken: cancellationToken))];
    }

    // Every feed configured, and every sink with deliveries (a built-in feed set up only in configuration has no row): kind:feed is the sink.
    public async Task<IReadOnlyList<(string Feed, bool Enabled, int Pending, DateTimeOffset? OldestPending, int Failed)>> FeedsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<(string, bool, int, DateTimeOffset?, int)>(new CommandDefinition(
            """
            WITH queue AS (
                SELECT sink, count(*) FILTER (WHERE status = 'pending')::int AS pending,
                       min(created_at) FILTER (WHERE status = 'pending') AS oldest,
                       count(*) FILTER (WHERE status = 'failed')::int AS failed
                FROM evt.delivery
                WHERE status IN ('pending', 'failed')
                GROUP BY sink)
            SELECT COALESCE(f.feed, split_part(q.sink, ':', 2)), COALESCE(f.enabled, true), COALESCE(q.pending, 0), q.oldest, COALESCE(q.failed, 0)
            FROM core.delivery_feed f
            FULL JOIN queue q ON q.sink = f.kind || ':' || f.feed
            ORDER BY 1
            """, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<(string Code, string Target, bool IsActive, bool Scheduled, string? LastOutcome, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt)>> ConnectionsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<(string, string, bool, bool, string?, DateTimeOffset?, DateTimeOffset?)>(new CommandDefinition(
            """
            SELECT code, target, is_active, (schedule_every_hours IS NOT NULL OR COALESCE(cardinality(schedule_daily_at), 0) > 0), last_run_outcome, last_run_at, next_run_at
            FROM core.material_connection
            ORDER BY code
            """, cancellationToken: cancellationToken))];
    }

    // Days the rollup service should have caught up with: changed more than a day ago and still waiting.
    public async Task<IReadOnlyList<(string SiteCode, int Days, DateOnly Oldest)>> StaleRollupsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string, int, DateTime)>(new CommandDefinition(
            """
            SELECT s.code, count(*)::int, min(d.production_date)::timestamp
            FROM ts.oee_rollup_day d JOIN core.equipment s ON s.id = d.site_id
            WHERE d.dirty_at IS NOT NULL AND d.dirty_at < now() - interval '1 day'
            GROUP BY s.code
            ORDER BY s.code
            """, cancellationToken: cancellationToken));
        return [.. rows.Select(r => (r.Item1, r.Item2, DateOnly.FromDateTime(r.Item3)))];
    }
}
