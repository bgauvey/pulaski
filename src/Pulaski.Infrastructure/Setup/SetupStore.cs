using Dapper;
using Npgsql;
using Pulaski.Application.Setup;
using Pulaski.Domain.Setup;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Setup;

/// <summary>Reads for the setup checklist (#139): one count per step, at the site.</summary>
internal sealed class SetupQueries(NpgsqlDataSource dataSource) : ISetupReadStore
{
    // Materials, conversions and BOMs are enterprise-wide; everything else is the site's. Without a site, only those count.
    private const string FactsSql = """
        SELECT
            (SELECT count(*) FROM core.equipment WHERE id = @SiteId AND level = 'site' AND is_active AND coalesce(time_zone, '') <> '')::int AS site,
            (SELECT count(*) FROM core.equipment WHERE site_id = @SiteId AND level <> 'site' AND can_run_job AND is_active)::int AS lines,
            (SELECT count(*) FROM core.material WHERE is_active)::int AS materials,
            (SELECT count(*) FROM core.material_uom_conversion WHERE is_active)::int AS units,
            (SELECT count(*) FROM core.equipment_capability c JOIN core.equipment e ON e.id = c.equipment_id
             WHERE e.site_id = @SiteId AND (c.effective_to IS NULL OR c.effective_to > now()))::int AS capabilities,
            (SELECT count(*) FROM core.shift_schedule WHERE site_id = @SiteId AND is_active)::int AS shifts,
            (SELECT count(*) FROM core.person WHERE site_id = @SiteId AND is_active)::int AS people,
            (SELECT count(*) FROM core.process p WHERE p.site_id = @SiteId AND p.is_active
             AND EXISTS (SELECT 1 FROM core.process_version v WHERE v.process_id = p.id AND v.status = 'released'))::int AS processes,
            (SELECT count(*) FROM core.bom b WHERE b.is_active
             AND EXISTS (SELECT 1 FROM core.bom_version v WHERE v.bom_id = b.id AND v.status = 'released'))::int AS boms,
            (SELECT count(*) FROM core.equipment WHERE site_id = @SiteId AND holds_inventory AND is_active)::int AS locations,
            (SELECT count(*) FROM core.reason_code WHERE is_active AND (site_id IS NULL OR site_id = @SiteId))::int AS reasons,
            (SELECT count(*) FROM core.machine_profile WHERE site_id = @SiteId AND is_active)::int AS machine_profiles,
            (SELECT count(*) FROM core.edge_node WHERE site_id = @SiteId AND is_active)::int AS edge_nodes,
            (SELECT count(DISTINCT a.app_user_id) FROM sec.role_assignment a JOIN sec.app_user u ON u.id = a.app_user_id
             WHERE a.scope_equipment_id = @SiteId AND u.is_active AND a.valid_from <= now() AND (a.valid_to IS NULL OR a.valid_to > now()))::int AS users,
            (SELECT count(*) FROM core.work_order WHERE site_id = @SiteId)::int AS work_orders,
            (SELECT count(*) FROM core.job_run r JOIN core.job j ON j.id = r.job_id WHERE j.site_id = @SiteId)::int AS first_job
        """;

    public async Task<string?> SiteCodeAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT code FROM core.equipment WHERE id = @Id AND level = 'site'", new { Id = siteId }, cancellationToken: cancellationToken));
    }

    public async Task<SetupFacts> FactsAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = (IDictionary<string, object>)await connection.QuerySingleAsync(new CommandDefinition(
            FactsSql, new { SiteId = siteId ?? Guid.Empty }, cancellationToken: cancellationToken));
        return new SetupFacts(SetupSteps.All.ToDictionary(s => s.Key, s => Convert.ToInt32(row[s.Key], System.Globalization.CultureInfo.InvariantCulture)));
    }

    public async Task<IReadOnlyList<SetupSkip>> SkipsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<SetupSkip>(new CommandDefinition(
            "SELECT step, is_skipped, changed_by, changed_at FROM core.setup_step_skip WHERE site_id = @SiteId", new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }
}

/// <summary>Writes for the setup checklist (#139).</summary>
internal sealed class SetupRepository(DbSession session) : ISetupRepository
{
    public async Task<bool> LockSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM core.equipment WHERE id = @Id AND level = 'site' FOR NO KEY UPDATE", new { Id = siteId }, cancellationToken) is not null;

    public Task UpsertSkipAsync(Guid siteId, SetupSkip skip, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.setup_step_skip (site_id, step, is_skipped, changed_by, changed_at) VALUES (@SiteId, @Step, @IsSkipped, @ChangedBy, @ChangedAt)
            ON CONFLICT (site_id, step) DO UPDATE SET is_skipped = EXCLUDED.is_skipped, changed_by = EXCLUDED.changed_by, changed_at = EXCLUDED.changed_at
            """, new { SiteId = siteId, skip.Step, skip.IsSkipped, skip.ChangedBy, skip.ChangedAt }, cancellationToken);
}
