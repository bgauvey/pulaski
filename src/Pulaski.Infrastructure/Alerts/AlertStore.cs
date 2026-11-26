using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Alerts;
using Pulaski.Contracts.Alerts;
using Pulaski.Domain.Alerts;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Alerts;

/// <summary>Alert subscriptions and the alerts sent (#121, D-109), in the command's transaction.</summary>
internal sealed class AlertRepository(DbSession session) : IAlertRepository
{
    public Task<Guid?> SiteOfAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT CASE WHEN level = 'site' THEN id ELSE site_id END FROM core.equipment WHERE id = @Id", new { Id = equipmentId }, cancellationToken);

    public Task<bool> ExistsAsync(Guid userId, Guid siteId, Guid? equipmentId, string kind, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM core.alert_subscription
                           WHERE user_id = @UserId AND site_id = @SiteId AND kind = @Kind AND equipment_id IS NOT DISTINCT FROM @EquipmentId)
            """,
            new { UserId = userId, SiteId = siteId, EquipmentId = equipmentId, Kind = kind }, cancellationToken);

    public async Task<Versioned<AlertSubscription>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Row>(
            """
            SELECT id, user_id, site_id, equipment_id, kind, minutes, is_active, xmin::text::bigint AS version
            FROM core.alert_subscription WHERE id = @Id FOR UPDATE
            """,
            new { Id = id }, cancellationToken) is { } r
            ? new Versioned<AlertSubscription>(new AlertSubscription(r.Id, r.UserId, r.SiteId, r.EquipmentId, r.Kind, r.Minutes, r.IsActive), r.Version)
            : null;

    public Task<long> InsertAsync(AlertSubscription subscription, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            INSERT INTO core.alert_subscription (id, user_id, site_id, equipment_id, kind, minutes, is_active, created_at, updated_at)
            VALUES (@Id, @UserId, @SiteId, @EquipmentId, @Kind, @Minutes, @IsActive, @At, @At)
            RETURNING xmin::text::bigint
            """,
            new { subscription.Id, subscription.UserId, subscription.SiteId, subscription.EquipmentId, subscription.Kind, subscription.Minutes, subscription.IsActive, At = at },
            cancellationToken);

    public Task<long> UpdateAsync(AlertSubscription subscription, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            "UPDATE core.alert_subscription SET minutes = @Minutes, is_active = @IsActive, updated_at = @At WHERE id = @Id RETURNING xmin::text::bigint",
            new { subscription.Id, subscription.Minutes, subscription.IsActive, At = at }, cancellationToken);

    public async Task<bool> MarkSentAsync(Guid subscriptionId, Guid subjectId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.ExecuteAsync(
            """
            INSERT INTO core.alert_sent (subscription_id, subject_id, sent_at) VALUES (@SubscriptionId, @SubjectId, @At)
            ON CONFLICT (subscription_id, subject_id) DO NOTHING
            """,
            new { SubscriptionId = subscriptionId, SubjectId = subjectId, At = at }, cancellationToken) == 1;

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public Guid SiteId { get; init; }
        public Guid? EquipmentId { get; init; }
        public string Kind { get; init; } = "";
        public int? Minutes { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }
}

/// <summary>Alert reads (#121, D-109): a user's subscriptions, and what is due to be sent.</summary>
internal sealed class AlertQueries(NpgsqlDataSource dataSource) : IAlertReadStore
{
    private const string Mine = """
        SELECT s.id, s.site_id, site.code AS site_code, s.equipment_id, e.code AS equipment_code, s.kind, s.minutes, s.is_active,
               s.xmin::text::bigint AS version
        FROM core.alert_subscription s
        JOIN core.equipment site ON site.id = s.site_id
        LEFT JOIN core.equipment e ON e.id = s.equipment_id
        WHERE s.user_id = @UserId
        ORDER BY site.code, s.kind, e.code NULLS FIRST
        """;

    // What is due: one branch per kind, each only for stops of machines and stops entered by hand as they are now (split, merged or retimed
    // pieces don't alert again) or open andon calls, at or below the subscription's equipment, reaching their moment since the subscription
    // was made and not yet sent.
    private const string DueSql = """
        WITH subs AS (
            SELECT s.id, s.kind, s.minutes, s.site_id, s.equipment_id, s.created_at, u.id AS user_id, u.idp_issuer, u.idp_subject, u.email,
                   u.display_name, site.code AS site_code, site.time_zone
            FROM core.alert_subscription s
            JOIN sec.app_user u ON u.id = s.user_id AND u.is_active AND coalesce(u.email, '') <> ''
            JOIN core.equipment site ON site.id = s.site_id
            WHERE s.is_active AND s.kind IN ('long_stop', 'uncoded_stop', 'andon_escalated', 'spc_alert', 'lot_hold', 'ncr_action_overdue')
        )
        -- A stop that reached the minutes, still going or not.
        SELECT s.id AS subscription_id, s.kind, s.minutes, d.id AS subject_id, s.user_id, s.idp_issuer AS issuer, s.idp_subject AS subject, s.email,
               s.display_name AS user_name, s.site_id, s.site_code, s.time_zone, e.code AS equipment_code, r.name AS what, NULL::text AS note,
               d.started_at AS start, d.ended_at AS "end", NULL::text AS item, NULL::uuid AS item_id
        FROM subs s
        JOIN ts.downtime d ON d.started_at >= @Since - interval '7 days'
        JOIN core.equipment e ON e.id = d.equipment_id AND e.site_id = s.site_id
        LEFT JOIN core.reason_code r ON r.id = d.effective_reason_code_id
        WHERE s.kind = 'long_stop'
          AND d.superseded_at IS NULL AND NOT d.is_micro_stop AND d.source IN ('machine', 'manual')
          AND d.started_at + make_interval(mins => s.minutes) BETWEEN greatest(@Since, s.created_at) AND @Now
          AND (d.ended_at IS NULL OR d.ended_at >= d.started_at + make_interval(mins => s.minutes))
          AND (s.equipment_id IS NULL OR d.equipment_id = s.equipment_id
               OR d.equipment_id IN (SELECT descendant_id FROM core.equipment_closure WHERE ancestor_id = s.equipment_id))
          AND NOT EXISTS (SELECT 1 FROM core.alert_sent a WHERE a.subscription_id = s.id AND a.subject_id = d.id)
        UNION ALL
        -- A stop still without a reason that many minutes after it ended.
        SELECT s.id, s.kind, s.minutes, d.id, s.user_id, s.idp_issuer, s.idp_subject, s.email, s.display_name, s.site_id, s.site_code, s.time_zone,
               e.code, NULL, NULL, d.started_at, d.ended_at, NULL, NULL
        FROM subs s
        JOIN ts.downtime d ON d.started_at >= @Since - interval '7 days'
        JOIN core.equipment e ON e.id = d.equipment_id AND e.site_id = s.site_id
        WHERE s.kind = 'uncoded_stop'
          AND d.superseded_at IS NULL AND NOT d.is_micro_stop AND d.source IN ('machine', 'manual') AND d.effective_reason_code_id IS NULL
          AND d.ended_at + make_interval(mins => s.minutes) BETWEEN greatest(@Since, s.created_at) AND @Now
          AND (s.equipment_id IS NULL OR d.equipment_id = s.equipment_id
               OR d.equipment_id IN (SELECT descendant_id FROM core.equipment_closure WHERE ancestor_id = s.equipment_id))
          AND NOT EXISTS (SELECT 1 FROM core.alert_sent a WHERE a.subscription_id = s.id AND a.subject_id = d.id)
        UNION ALL
        -- A call for help still unanswered past its kind's escalation time.
        SELECT s.id, s.kind, s.minutes, c.id, s.user_id, s.idp_issuer, s.idp_subject, s.email, s.display_name, s.site_id, s.site_code, s.time_zone,
               l.code, t.name, c.note, c.raised_at, NULL, NULL, NULL
        FROM subs s
        JOIN core.andon_call c ON c.site_id = s.site_id AND c.status = 'open'
        JOIN core.andon_type t ON t.id = c.andon_type_id
        JOIN core.equipment l ON l.id = c.line_id
        WHERE s.kind = 'andon_escalated'
          AND c.raised_at + make_interval(mins => t.escalate_after_minutes) BETWEEN greatest(@Since, s.created_at) AND @Now
          AND (s.equipment_id IS NULL OR c.line_id = s.equipment_id
               OR c.line_id IN (SELECT descendant_id FROM core.equipment_closure WHERE ancestor_id = s.equipment_id))
          AND NOT EXISTS (SELECT 1 FROM core.alert_sent a WHERE a.subscription_id = s.id AND a.subject_id = c.id)
        UNION ALL
        -- An SPC alert raised (#165): on its line, or on the site for results without one (then only whole-site subscriptions).
        SELECT s.id, s.kind, s.minutes, a.id, s.user_id, s.idp_issuer, s.idp_subject, s.email, s.display_name, s.site_id, s.site_code, s.time_zone,
               l.code, q.code || ' ' || q.name, m.code, a.raised_at, NULL, NULL, NULL
        FROM subs s
        JOIN core.spc_alert a ON a.site_id = s.site_id
        JOIN core.quality_characteristic q ON q.id = a.characteristic_id
        JOIN core.material m ON m.id = a.material_id
        LEFT JOIN core.equipment l ON l.id = a.equipment_id
        WHERE s.kind = 'spc_alert'
          AND a.raised_at BETWEEN greatest(@Since, s.created_at) AND @Now
          AND (s.equipment_id IS NULL OR a.equipment_id = s.equipment_id
               OR a.equipment_id IN (SELECT descendant_id FROM core.equipment_closure WHERE ancestor_id = s.equipment_id))
          AND NOT EXISTS (SELECT 1 FROM core.alert_sent x WHERE x.subscription_id = s.id AND x.subject_id = a.id)
        UNION ALL
        -- A lot put on hold (#165): the status change, by where the lot is now.
        SELECT s.id, s.kind, s.minutes, ev.id, s.user_id, s.idp_issuer, s.idp_subject, s.email, s.display_name, s.site_id, s.site_code, s.time_zone,
               loc.code, concat_ws(': ', r.name, ev.comment), m.code, ev.occurred_at, NULL, coalesce(lot.lot_number, 'balance at ' || loc.code), lot.id
        FROM subs s
        JOIN ts.lot_event ev ON ev.occurred_at >= @Since AND ev.event_kind = 'status_changed' AND ev.to_status = 'on_hold' AND ev.reverses_event_id IS NULL
        JOIN core.lot lot ON lot.id = ev.lot_id AND lot.site_id = s.site_id
        JOIN core.material m ON m.id = lot.material_id
        LEFT JOIN core.equipment loc ON loc.id = lot.location_equipment_id
        LEFT JOIN core.reason_code r ON r.id = ev.reason_code_id
        WHERE s.kind = 'lot_hold'
          AND ev.occurred_at BETWEEN greatest(@Since, s.created_at) AND @Now
          AND (s.equipment_id IS NULL OR lot.location_equipment_id = s.equipment_id
               OR lot.location_equipment_id IN (SELECT descendant_id FROM core.equipment_closure WHERE ancestor_id = s.equipment_id))
          AND NOT EXISTS (SELECT 1 FROM core.alert_sent x WHERE x.subscription_id = s.id AND x.subject_id = ev.id)
        UNION ALL
        -- An NCR action still open when its due day ended (#133b), at the site's midnight; whole-site subscriptions only.
        SELECT s.id, s.kind, s.minutes, a.id, s.user_id, s.idp_issuer, s.idp_subject, s.email, s.display_name, s.site_id, s.site_code, s.time_zone,
               NULL, a.description, n.number || ' ' || n.title, due.at, NULL, o.display_name, n.id
        FROM subs s
        JOIN core.nonconformance n ON n.site_id = s.site_id AND n.status <> 'closed'
        JOIN core.nonconformance_action a ON a.ncr_id = n.id AND a.status = 'open' AND a.due_on IS NOT NULL
        LEFT JOIN sec.app_user o ON o.id = a.owner_user_id
        CROSS JOIN LATERAL (SELECT ((a.due_on + 1)::timestamp AT TIME ZONE coalesce(s.time_zone, 'UTC')) AS at) due
        WHERE s.kind = 'ncr_action_overdue' AND s.equipment_id IS NULL
          AND due.at BETWEEN greatest(@Since, s.created_at) AND @Now
          AND NOT EXISTS (SELECT 1 FROM core.alert_sent x WHERE x.subscription_id = s.id AND x.subject_id = a.id)
        """;

    private const string SubscribersSql = """
        SELECT s.id AS subscription_id, s.kind, s.minutes, s.equipment_id, u.id AS user_id, u.idp_issuer AS issuer, u.idp_subject AS subject, u.email,
               u.display_name AS user_name, s.site_id, site.code AS site_code, site.time_zone
        FROM core.alert_subscription s
        JOIN sec.app_user u ON u.id = s.user_id AND u.is_active AND coalesce(u.email, '') <> ''
        JOIN core.equipment site ON site.id = s.site_id
        WHERE s.is_active AND s.kind = @Kind
        ORDER BY s.site_id, s.equipment_id, s.id
        """;

    public async Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT nullif(email, '') FROM sec.app_user WHERE id = @Id", new { Id = userId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AlertSubscriptionDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<AlertSubscriptionDto>(new CommandDefinition(Mine, new { UserId = userId }, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<AlertSubscriber>> ListSubscribersAsync(string kind, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<AlertSubscriber>(new CommandDefinition(SubscribersSql, new { Kind = kind }, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<AlertDue>> ListDueAsync(DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<AlertDue>(new CommandDefinition(DueSql, new { Since = since, Now = now }, cancellationToken: cancellationToken))];
    }
}
