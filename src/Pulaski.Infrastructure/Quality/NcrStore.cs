using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Quality;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Quality;

/// <summary>Nonconformance reports' writes (#133, D-124), in the command's transaction.</summary>
internal sealed class NcrRepository(DbSession session) : INcrRepository
{
    private const string Columns = """
        id, site_id, number, title, description, severity, status, source_kind, source_id, source_at, owner_user_id, raised_by, raised_at,
        root_cause, whys, disposition, closing_note, closed_at, closed_by, xmin::text::bigint AS version
        """;

    public Task<long> NextNumberAsync(CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>("SELECT nextval('core.nonconformance_no')", null, cancellationToken);

    public async Task<NcrSource?> SourceAsync(string kind, Guid id, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        // Each source's site, the lots it concerns and the runs: a lot's making run; a result's lot and run; an alert's latest result's; a checklist's run.
        var sql = kind switch
        {
            NcrSources.LotHold or NcrSources.LotRejection => """
                SELECT l.site_id, ARRAY[l.id] AS lots,
                       COALESCE((SELECT array_agg(DISTINCT e.run_id) FROM ts.lot_event e WHERE e.lot_id = l.id AND e.event_kind = 'produced' AND e.run_id IS NOT NULL), '{}') AS runs
                FROM core.lot l WHERE l.id = @Id
                """,
            NcrSources.QualityResult => """
                SELECT q.site_id, CASE WHEN q.lot_id IS NULL THEN '{}'::uuid[] ELSE ARRAY[q.lot_id] END AS lots,
                       CASE WHEN q.run_id IS NULL THEN '{}'::uuid[] ELSE ARRAY[q.run_id] END AS runs
                FROM ts.quality_sample q WHERE q.id = @Id AND (@At::timestamptz IS NULL OR q.occurred_at = @At)
                """,
            NcrSources.SpcAlert => """
                SELECT a.site_id, CASE WHEN q.lot_id IS NULL THEN '{}'::uuid[] ELSE ARRAY[q.lot_id] END AS lots,
                       CASE WHEN q.run_id IS NULL THEN '{}'::uuid[] ELSE ARRAY[q.run_id] END AS runs
                FROM core.spc_alert a
                LEFT JOIN ts.quality_sample q ON q.id = a.last_sample_id AND q.occurred_at = a.last_sample_at
                WHERE a.id = @Id
                """,
            NcrSources.Run => """
                SELECT j.site_id,
                       COALESCE((SELECT array_agg(DISTINCT e.lot_id) FROM ts.lot_event e WHERE e.run_id = r.id AND e.event_kind = 'produced'), '{}') AS lots,
                       ARRAY[r.id] AS runs
                FROM core.job_run r JOIN core.job j ON j.id = r.job_id WHERE r.id = @Id
                """,
            NcrSources.Checklist => """
                SELECT r.site_id, '{}'::uuid[] AS lots, CASE WHEN r.run_id IS NULL THEN '{}'::uuid[] ELSE ARRAY[r.run_id] END AS runs
                FROM core.checklist_response r WHERE r.id = @Id
                """,
            _ => null,
        };
        if (sql is null)
        {
            return null;
        }

        var row = await session.QuerySingleOrDefaultAsync<SourceRow>(sql, new { Id = id, At = at }, cancellationToken);
        return row is null ? null : new NcrSource(row.SiteId, row.Lots, row.Runs);
    }

    public async Task<IReadOnlyList<Guid>> LotsAtAsync(Guid siteId, IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        lotIds.Count == 0 ? [] : [.. await session.QueryAsync<Guid>(
            "SELECT id FROM core.lot WHERE id = ANY(@Ids) AND site_id = @SiteId", new { Ids = lotIds.ToArray(), SiteId = siteId }, cancellationToken)];

    public async Task<IReadOnlyList<Guid>> FindLotsAsync(Guid siteId, string lotNumber, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<Guid>(
            "SELECT id FROM core.lot WHERE site_id = @SiteId AND lot_number = @Number ORDER BY id", new { SiteId = siteId, Number = lotNumber }, cancellationToken)];

    public Task<bool> IsSiteUserAsync(Guid userId, Guid siteId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM sec.app_user u JOIN sec.role_assignment a ON a.app_user_id = u.id
                WHERE u.id = @UserId AND u.is_active AND a.valid_from <= now() AND (a.valid_to IS NULL OR a.valid_to > now())
                  AND (a.scope_equipment_id IS NULL OR a.scope_equipment_id = @SiteId))
            """, new { UserId = userId, SiteId = siteId }, cancellationToken);

    public Task InsertAsync(Nonconformance ncr, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.nonconformance (id, site_id, number, title, description, severity, status, source_kind, source_id, source_at, owner_user_id, raised_by, raised_at)
            VALUES (@Id, @SiteId, @Number, @Title, @Description, @Severity, @Status, @SourceKind, @SourceId, @SourceAt, @OwnerUserId, @RaisedBy, @RaisedAt)
            """, ncr, cancellationToken);

    public async Task<Versioned<Nonconformance>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM core.nonconformance WHERE id = @Id FOR UPDATE", new { Id = id }, cancellationToken) is { } r
            ? new Versioned<Nonconformance>(r.ToDomain(), r.Version)
            : null;

    public Task<long> UpdateAsync(Nonconformance ncr, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            UPDATE core.nonconformance
            SET title = @Title, description = @Description, severity = @Severity, status = @Status, owner_user_id = @OwnerUserId, root_cause = @RootCause,
                whys = @Whys, disposition = @Disposition, closing_note = @ClosingNote, closed_at = @ClosedAt, closed_by = @ClosedBy, updated_at = @At
            WHERE id = @Id
            RETURNING xmin::text::bigint
            """,
            new
            {
                ncr.Id,
                ncr.Title,
                ncr.Description,
                ncr.Severity,
                ncr.Status,
                ncr.OwnerUserId,
                ncr.RootCause,
                Whys = (ncr.Whys ?? []).ToArray(),
                ncr.Disposition,
                ncr.ClosingNote,
                ncr.ClosedAt,
                ncr.ClosedBy,
                At = at,
            }, cancellationToken);

    public async Task LinkAsync(Guid ncrId, IReadOnlyCollection<Guid> lotIds, IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken)
    {
        if (lotIds.Count > 0)
        {
            await session.ExecuteAsync(
                "INSERT INTO core.nonconformance_lot (ncr_id, lot_id) SELECT @NcrId, unnest(@Ids) ON CONFLICT DO NOTHING", new { NcrId = ncrId, Ids = lotIds.ToArray() }, cancellationToken);
        }

        if (runIds.Count > 0)
        {
            await session.ExecuteAsync(
                "INSERT INTO core.nonconformance_run (ncr_id, run_id) SELECT @NcrId, unnest(@Ids) ON CONFLICT DO NOTHING", new { NcrId = ncrId, Ids = runIds.ToArray() }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<NcrAction>> ListActionsAsync(Guid ncrId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<ActionRow>(
            "SELECT id, ncr_id, kind, description, owner_user_id, due_on::timestamp AS due_on, status, done_at, done_by, note FROM core.nonconformance_action WHERE ncr_id = @Id ORDER BY created_at",
            new { Id = ncrId }, cancellationToken)).Select(r => r.ToDomain())];

    public Task InsertActionAsync(NcrAction action, string by, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.nonconformance_action (id, ncr_id, kind, description, owner_user_id, due_on, status, created_by)
            VALUES (@Id, @NcrId, @Kind, @Description, @OwnerUserId, @DueOn::date, @Status, @By)
            """,
            new { action.Id, action.NcrId, action.Kind, action.Description, action.OwnerUserId, DueOn = action.DueOn?.ToDateTime(TimeOnly.MinValue), action.Status, By = by },
            cancellationToken);

    public Task UpdateActionAsync(NcrAction action, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "UPDATE core.nonconformance_action SET status = @Status, done_at = @DoneAt, done_by = @DoneBy, note = @Note WHERE id = @Id",
            new { action.Id, action.Status, action.DoneAt, action.DoneBy, action.Note }, cancellationToken);

    private sealed class SourceRow
    {
        public Guid SiteId { get; init; }
        public Guid[] Lots { get; init; } = [];
        public Guid[] Runs { get; init; } = [];
    }

    private sealed class ActionRow
    {
        public Guid Id { get; init; }
        public Guid NcrId { get; init; }
        public string Kind { get; init; } = "";
        public string Description { get; init; } = "";
        public Guid? OwnerUserId { get; init; }
        public DateTime? DueOn { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? DoneAt { get; init; }
        public string? DoneBy { get; init; }
        public string? Note { get; init; }

        public NcrAction ToDomain() => new(Id, NcrId, Kind, Description, OwnerUserId, DueOn is { } d ? DateOnly.FromDateTime(d) : null, Status, DoneAt, DoneBy, Note);
    }

    internal sealed class Row
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Number { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Status { get; init; } = "";
        public string SourceKind { get; init; } = "";
        public Guid? SourceId { get; init; }
        public DateTimeOffset? SourceAt { get; init; }
        public Guid? OwnerUserId { get; init; }
        public string RaisedBy { get; init; } = "";
        public DateTimeOffset RaisedAt { get; init; }
        public string? RootCause { get; init; }
        public string[] Whys { get; init; } = [];
        public string? Disposition { get; init; }
        public string? ClosingNote { get; init; }
        public DateTimeOffset? ClosedAt { get; init; }
        public string? ClosedBy { get; init; }
        public long Version { get; init; }

        public Nonconformance ToDomain() => new(
            Id, SiteId, Number, Title, Description, Severity, Status, SourceKind, SourceId, SourceAt, OwnerUserId, RaisedBy, RaisedAt, RootCause, Whys, Disposition,
            ClosingNote, ClosedAt, ClosedBy);
    }
}

/// <summary>Nonconformance reports' reads (#133).</summary>
internal sealed class NcrQueries(NpgsqlDataSource dataSource) : INcrReadStore
{
    public async Task<IReadOnlyList<NcrSummaryDto>> ListAsync(Guid? siteId, string? status, Guid? lotId, DateOnly today, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<NcrSummaryDto>(new CommandDefinition(
            """
            SELECT n.id, n.site_id, n.number, n.title, n.severity, n.status, n.source_kind, o.display_name AS owner_name, n.raised_at,
                   (SELECT count(*)::int FROM core.nonconformance_action a WHERE a.ncr_id = n.id AND a.status = 'open') AS open_actions,
                   (SELECT count(*)::int FROM core.nonconformance_action a WHERE a.ncr_id = n.id AND a.status = 'open' AND a.due_on < @Today) AS overdue_actions
            FROM core.nonconformance n
            LEFT JOIN sec.app_user o ON o.id = n.owner_user_id
            WHERE (@SiteId::uuid IS NULL OR n.site_id = @SiteId)
              AND (@Status::text IS NULL OR (@Status = 'open_any' AND n.status <> 'closed') OR n.status = @Status)
              AND (@LotId::uuid IS NULL OR EXISTS (SELECT 1 FROM core.nonconformance_lot l WHERE l.ncr_id = n.id AND l.lot_id = @LotId))
            ORDER BY n.status = 'closed', n.raised_at DESC
            LIMIT 500
            """, new { SiteId = siteId, Status = status, LotId = lotId, Today = today.ToDateTime(TimeOnly.MinValue) }, cancellationToken: cancellationToken))];
    }

    public async Task<NcrDto?> GetAsync(Guid id, DateOnly today, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var head = await connection.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(
            $"""
            SELECT n.id, n.site_id, s.time_zone AS site_time_zone, n.number, n.title, n.description, n.severity, n.status, n.source_kind, n.source_id, n.source_at,
                   n.owner_user_id, o.display_name AS owner_name, n.raised_at, {Instructions.InstructionSql.UserName("n.raised_by")} AS raised_by_name,
                   n.root_cause, n.whys, n.disposition, n.closing_note, n.closed_at, {Instructions.InstructionSql.UserName("n.closed_by")} AS closed_by_name,
                   n.xmin::text::bigint AS version
            FROM core.nonconformance n
            JOIN core.equipment s ON s.id = n.site_id
            LEFT JOIN sec.app_user o ON o.id = n.owner_user_id
            WHERE n.id = @Id
            """, new { Id = id }, cancellationToken: cancellationToken));
        if (head is null)
        {
            return null;
        }

        var lots = await connection.QueryAsync<NcrLotDto>(new CommandDefinition(
            """
            SELECT l.id AS lot_id, COALESCE(l.lot_number, '@' || COALESCE(e.code, '?')) AS label, m.code AS material_code, l.status,
                   l.quantity, u.code AS uom_code, l.xmin::text::bigint AS version
            FROM core.nonconformance_lot nl JOIN core.lot l ON l.id = nl.lot_id JOIN core.material m ON m.id = l.material_id
            JOIN core.uom u ON u.id = l.uom_id
            LEFT JOIN core.equipment e ON e.id = l.location_equipment_id
            WHERE nl.ncr_id = @Id ORDER BY label
            """, new { Id = id }, cancellationToken: cancellationToken));
        var runs = await connection.QueryAsync<NcrRunDto>(new CommandDefinition(
            """
            SELECT r.id AS run_id, j.job_number, r.run_no, e.code AS line_code
            FROM core.nonconformance_run nr JOIN core.job_run r ON r.id = nr.run_id JOIN core.job j ON j.id = r.job_id JOIN core.equipment e ON e.id = r.equipment_id
            WHERE nr.ncr_id = @Id ORDER BY r.started_at
            """, new { Id = id }, cancellationToken: cancellationToken));
        var actions = await connection.QueryAsync<ActionRow>(new CommandDefinition(
            $"""
            SELECT a.id, a.kind, a.description, a.owner_user_id, o.display_name AS owner_name, a.due_on::timestamp AS due_on, a.status, a.done_at,
                   {Instructions.InstructionSql.UserName("a.done_by")} AS done_by_name, a.note
            FROM core.nonconformance_action a LEFT JOIN sec.app_user o ON o.id = a.owner_user_id
            WHERE a.ncr_id = @Id ORDER BY a.created_at
            """, new { Id = id }, cancellationToken: cancellationToken));
        return new NcrDto
        {
            Id = head.Id,
            SiteId = head.SiteId,
            SiteTimeZone = head.SiteTimeZone,
            Number = head.Number,
            Title = head.Title,
            Description = head.Description,
            Severity = head.Severity,
            Status = head.Status,
            SourceKind = head.SourceKind,
            SourceId = head.SourceId,
            SourceAt = head.SourceAt,
            OwnerUserId = head.OwnerUserId,
            OwnerName = head.OwnerName,
            RaisedAt = head.RaisedAt,
            RaisedByName = head.RaisedByName,
            RootCause = head.RootCause,
            Whys = head.Whys,
            Disposition = head.Disposition,
            ClosingNote = head.ClosingNote,
            ClosedAt = head.ClosedAt,
            ClosedByName = head.ClosedByName,
            Version = head.Version,
            Lots = [.. lots],
            Runs = [.. runs],
            Actions = [.. actions.Select(a => new NcrActionDto
            {
                Id = a.Id,
                Kind = a.Kind,
                Description = a.Description,
                OwnerUserId = a.OwnerUserId,
                OwnerName = a.OwnerName,
                DueOn = a.DueOn is { } d ? DateOnly.FromDateTime(d) : null,
                Status = a.Status,
                DoneAt = a.DoneAt,
                DoneByName = a.DoneByName,
                Note = a.Note,
                IsOverdue = a.Status == NcrActionStatus.Open && a.DueOn is { } due && DateOnly.FromDateTime(due) < today,
            })],
        };
    }

    public async Task<IReadOnlyList<NcrAssigneeDto>> AssigneesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<NcrAssigneeDto>(new CommandDefinition(
            """
            SELECT DISTINCT u.id, u.display_name AS name
            FROM sec.app_user u JOIN sec.role_assignment a ON a.app_user_id = u.id
            WHERE u.is_active AND a.valid_from <= now() AND (a.valid_to IS NULL OR a.valid_to > now())
              AND (a.scope_equipment_id IS NULL OR a.scope_equipment_id = @SiteId)
            ORDER BY name
            """, new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }

    private sealed class HeadRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string? SiteTimeZone { get; init; }
        public string Number { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Status { get; init; } = "";
        public string SourceKind { get; init; } = "";
        public Guid? SourceId { get; init; }
        public DateTimeOffset? SourceAt { get; init; }
        public Guid? OwnerUserId { get; init; }
        public string? OwnerName { get; init; }
        public DateTimeOffset RaisedAt { get; init; }
        public string? RaisedByName { get; init; }
        public string? RootCause { get; init; }
        public string[] Whys { get; init; } = [];
        public string? Disposition { get; init; }
        public string? ClosingNote { get; init; }
        public DateTimeOffset? ClosedAt { get; init; }
        public string? ClosedByName { get; init; }
        public long Version { get; init; }
    }

    private sealed class ActionRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = "";
        public string Description { get; init; } = "";
        public Guid? OwnerUserId { get; init; }
        public string? OwnerName { get; init; }
        public DateTime? DueOn { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? DoneAt { get; init; }
        public string? DoneByName { get; init; }
        public string? Note { get; init; }
    }
}
