using Dapper;
using Npgsql;
using Pulaski.Application.Inbox;
using Pulaski.Contracts.Inbox;

namespace Pulaski.Infrastructure.Inbox;

/// <summary>What waits at sites (#123, D-112), from the same rows the deciding pages read.</summary>
internal sealed class InboxQueries(NpgsqlDataSource dataSource) : IInboxReadStore
{
    private const string Sites = "SELECT id, code FROM core.equipment WHERE level = 'site' AND is_active ORDER BY code";

    // Partial batches made to the exact rule, held until someone approves or rejects them (D-036): decided on the job's page.
    private const string PartialBatches = """
        SELECT 'partial_batch' AS kind, 'partial_batch:' || p.id AS key,
               'Partial batch of ' || m.code || ' on ' || j.job_number AS title,
               trim(to_char(p.quantity, 'FM999999999990.######')) || ' ' || u.code || ' of a ' || trim(to_char(p.batch_size, 'FM999999999990.######'))
                   || ' batch, lot ' || coalesce(l.lot_number, '?') AS detail,
               j.site_id, s.code AS site_code, p.occurred_at AS since, 'jobs/' || j.id AS link
        FROM core.partial_batch p
        JOIN core.job j ON j.id = p.job_id
        JOIN core.lot l ON l.id = p.lot_id
        JOIN core.material m ON m.id = l.material_id
        JOIN core.uom u ON u.id = p.uom_id
        JOIN core.equipment s ON s.id = j.site_id
        WHERE p.status = 'pending' AND j.site_id = ANY(@SiteIds)
        """;

    // Planning rates proposed from run history, waiting for review (D-045).
    private const string RateProposals = """
        SELECT 'rate_proposal' AS kind, 'rate_proposal:' || p.id AS key,
               'Planning rate for ' || m.code || ' on ' || e.code AS title,
               'Proposed ' || trim(to_char(p.proposed_rate_per_hour, 'FM999999999990.###')) || ' ' || u.code || '/h, now '
                   || trim(to_char(p.effective_rate_per_hour, 'FM999999999990.###')) || coalesce('; ' || p.review_reason, '') AS detail,
               e.site_id, s.code AS site_code, p.proposed_at AS since, 'rate-proposals' AS link
        FROM core.run_standard_proposal p
        JOIN core.run_standard r ON r.id = p.run_standard_id
        JOIN core.equipment e ON e.id = r.equipment_id
        JOIN core.material m ON m.id = r.material_id
        JOIN core.uom u ON u.id = p.uom_id
        JOIN core.equipment s ON s.id = e.site_id
        WHERE p.status = 'pending' AND e.site_id = ANY(@SiteIds)
        """;

    // Work orders the ERP changed after they were dispatched, waiting to be accepted or rejected (D-066).
    private const string ErpChanges = """
        SELECT 'erp_change' AS kind, 'erp_change:' || w.id AS key,
               'ERP change to ' || w.order_number AS title,
               'From ' || w.external_system || '; accept or reject it on Work orders' AS detail,
               w.site_id, s.code AS site_code, w.updated_at AS since, 'work-orders' AS link
        FROM core.work_order w
        JOIN core.equipment s ON s.id = w.site_id
        WHERE w.erp_change_pending AND w.site_id = ANY(@SiteIds)
        """;

    // Control charts that signalled and wait to be closed with a cause (D-099).
    private const string SpcAlerts = """
        SELECT 'spc_alert' AS kind, 'spc_alert:' || a.id AS key,
               'SPC alert: ' || q.code || ' on ' || m.code || coalesce(' (' || l.code || ')', '') AS title,
               a.breaks || CASE WHEN a.breaks = 1 THEN ' result' ELSE ' results' END || ' broke a rule; close it with the cause' AS detail,
               a.site_id, s.code AS site_code, a.raised_at AS since, 'quality/alerts' AS link
        FROM core.spc_alert a
        JOIN core.quality_characteristic q ON q.id = a.characteristic_id
        JOIN core.material m ON m.id = a.material_id
        LEFT JOIN core.equipment l ON l.id = a.equipment_id
        JOIN core.equipment s ON s.id = a.site_id
        WHERE a.status = 'open' AND a.site_id = ANY(@SiteIds)
        """;

    // Maintenance requests nobody has started yet (#127, D-115): worked on the Machines page. The title is the request's
    // number and equipment, the detail what was asked, so neither needs translating.
    private const string Maintenance = """
        SELECT 'maintenance' AS kind, 'maintenance:' || r.id AS key, r.number || ' · ' || e.code AS title, r.description AS detail,
               r.site_id, s.code AS site_code, r.raised_at AS since, 'machines?tab=maintenance' AS link
        FROM core.maintenance_request r
        JOIN core.equipment e ON e.id = r.equipment_id
        JOIN core.equipment s ON s.id = r.site_id
        WHERE r.status = 'open' AND r.site_id = ANY(@SiteIds)
        """;

    public async Task<IReadOnlyList<(Guid Id, string Code)>> ListSitesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<(Guid, string)>(new CommandDefinition(Sites, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<InboxItemDto>> ListAsync(string kind, IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken)
    {
        var sql = kind switch
        {
            InboxKinds.PartialBatch => PartialBatches,
            InboxKinds.RateProposal => RateProposals,
            InboxKinds.ErpChange => ErpChanges,
            InboxKinds.SpcAlert => SpcAlerts,
            InboxKinds.Maintenance => Maintenance,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an inbox kind read from SQL."),
        };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<InboxItemDto>(new CommandDefinition(sql, new { SiteIds = siteIds.ToArray() }, cancellationToken: cancellationToken))];
    }
}
