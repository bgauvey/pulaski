using System.Globalization;
using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Materials;

/// <summary>Connections to materials and people sources (D-068, #71) and their runs, in the command's transaction.</summary>
internal sealed class MaterialConnectionStore(DbSession session) : IMaterialConnectionStore
{
    private const string Columns = """
        id, code, name, target, site_id, kind, base_url, entity_set, filter, external_system, field_map::text AS field_map, is_active, next_link_path,
        request_body, soap_action, query, schedule_every_hours, schedule_daily_at::text[] AS schedule_daily_at, next_run_at, create_classes,
        switch_off_missing, xmin::text::bigint AS version
        """;

    private const string Insert = """
        INSERT INTO core.material_connection (id, code, name, target, site_id, kind, base_url, entity_set, filter, external_system, field_map, is_active,
                                              next_link_path, request_body, soap_action, query, schedule_every_hours, schedule_daily_at, next_run_at,
                                              create_classes, switch_off_missing, created_at, updated_at)
        VALUES (@Id, @Code, @Name, @Target, @SiteId, @Kind, @BaseUrl, @EntitySet, @Filter, @ExternalSystem, @FieldMap::jsonb, @IsActive, @NextLinkPath,
                @RequestBody, @SoapAction, @Query, @EveryHours, @DailyAt::time[], @NextRunAt, @CreateClasses, @SwitchOffMissing, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.material_connection
        SET name = @Name, base_url = @BaseUrl, entity_set = @EntitySet, filter = @Filter, external_system = @ExternalSystem,
            field_map = @FieldMap::jsonb, is_active = @IsActive, next_link_path = @NextLinkPath, request_body = @RequestBody, soap_action = @SoapAction,
            query = @Query, schedule_every_hours = @EveryHours, schedule_daily_at = @DailyAt::time[], next_run_at = @NextRunAt,
            create_classes = @CreateClasses, switch_off_missing = @SwitchOffMissing, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string InsertRun = """
        INSERT INTO ts.connection_run (id, started_at, finished_at, connection_id, trigger, outcome, created_count, updated_count, unchanged_count,
                                       error_count, summary, actor)
        VALUES (@Id, @StartedAt, @FinishedAt, @ConnectionId, @Trigger, @Outcome, @Created, @Updated, @Unchanged, @Errors, @Summary, @Actor);
        UPDATE core.material_connection
        SET last_run_at = @FinishedAt, last_run_by = @Actor, last_run_summary = left(@Summary, 1000), last_run_outcome = @Outcome
        WHERE id = @ConnectionId;
        """;

    private const string Runs = """
        SELECT id, connection_id, started_at, finished_at, trigger, outcome, created_count AS created, updated_count AS updated,
               unchanged_count AS unchanged, error_count AS errors, summary, actor
        FROM ts.connection_run
        WHERE connection_id = @Id
        ORDER BY started_at DESC
        LIMIT @Limit
        """;

    private const string Due = $"""
        SELECT {Columns} FROM core.material_connection
        WHERE is_active AND next_run_at IS NOT NULL AND next_run_at <= @Now
        ORDER BY next_run_at, id
        """;

    // Only if it is still due as read: a save or another instance moving it first wins.
    private const string Claim = """
        UPDATE core.material_connection SET next_run_at = @Following
        WHERE id = @Id AND is_active AND next_run_at = @Due
        RETURNING id
        """;

    public async Task<IReadOnlyList<Versioned<MaterialConnection>>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<Row>($"SELECT {Columns} FROM core.material_connection ORDER BY code", null, cancellationToken)).Select(r => r.ToVersioned())];

    public async Task<Versioned<MaterialConnection>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM core.material_connection WHERE id = @Id FOR UPDATE", new { Id = id }, cancellationToken))?.ToVersioned();

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM core.material_connection WHERE code = @Code)", new { Code = code }, cancellationToken);

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site' FOR SHARE)", new { Id = siteId }, cancellationToken);

    public Task<long> InsertAsync(MaterialConnection connection, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Args(connection, at), cancellationToken);

    public Task<long> UpdateAsync(MaterialConnection connection, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, Args(connection, at), cancellationToken);

    public Task RecordRunAsync(ConnectionRunRecord run, CancellationToken cancellationToken) =>
        session.ExecuteAsync(InsertRun, run, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, LastConnectionRun>> LastRunsAsync(CancellationToken cancellationToken) =>
        (await session.QueryAsync<LastRunRow>(
            "SELECT id, last_run_at, last_run_by, last_run_summary, last_run_outcome FROM core.material_connection WHERE last_run_at IS NOT NULL", null, cancellationToken))
        .ToDictionary(r => r.Id, r => new LastConnectionRun(r.LastRunAt, r.LastRunBy, r.LastRunSummary, r.LastRunOutcome));

    public async Task<IReadOnlyList<ConnectionRunRecord>> ListRunsAsync(Guid connectionId, int limit, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<RunRow>(Runs, new { Id = connectionId, Limit = limit }, cancellationToken))
            .Select(r => new ConnectionRunRecord(r.Id, r.ConnectionId, r.StartedAt, r.FinishedAt, r.Trigger, r.Outcome, r.Created, r.Updated, r.Unchanged, r.Errors, r.Summary, r.Actor))];

    public async Task<IReadOnlyList<MaterialConnection>> ListDueAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<Row>(Due, new { Now = now }, cancellationToken)).Select(r => r.ToVersioned().Value)];

    public async Task<bool> ClaimAsync(Guid id, DateTimeOffset due, DateTimeOffset? following, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<Guid?>(Claim, new { Id = id, Due = due, Following = following }, cancellationToken)) is not null;

    private static object Args(MaterialConnection c, DateTimeOffset at) => new
    {
        c.Id,
        c.Code,
        c.Name,
        c.Target,
        c.SiteId,
        c.Kind,
        c.BaseUrl,
        c.EntitySet,
        c.Filter,
        c.ExternalSystem,
        FieldMap = JsonSerializer.Serialize(c.FieldMap),
        c.IsActive,
        c.NextLinkPath,
        c.RequestBody,
        c.SoapAction,
        c.Query,
        c.Schedule.EveryHours,
        DailyAt = c.Schedule.DailyAt.Count == 0 ? null : c.Schedule.DailyAt.Select(t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).ToArray(),
        c.NextRunAt,
        c.CreateClasses,
        c.SwitchOffMissing,
        At = at,
    };

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Target { get; init; } = "";
        public Guid? SiteId { get; init; }
        public string Kind { get; init; } = "";
        public string? BaseUrl { get; init; }
        public string EntitySet { get; init; } = "";
        public string? Filter { get; init; }
        public string ExternalSystem { get; init; } = "";
        public string FieldMap { get; init; } = "{}";
        public bool IsActive { get; init; }
        public string? NextLinkPath { get; init; }
        public string? RequestBody { get; init; }
        public string? SoapAction { get; init; }
        public string? Query { get; init; }
        public short? ScheduleEveryHours { get; init; }
        public string[]? ScheduleDailyAt { get; init; }
        public DateTimeOffset? NextRunAt { get; init; }
        public bool CreateClasses { get; init; }
        public bool SwitchOffMissing { get; init; }
        public long Version { get; init; }

        public Versioned<MaterialConnection> ToVersioned() => new(
            new MaterialConnection(Id, Code, Name, Kind, BaseUrl, EntitySet, Filter, ExternalSystem,
                JsonSerializer.Deserialize<Dictionary<string, string>>(FieldMap) ?? [], IsActive, NextLinkPath, RequestBody, SoapAction, Query)
            {
                Target = Target,
                SiteId = SiteId,
                Schedule = new ConnectionScheduleSpec(
                    ScheduleEveryHours, [.. (ScheduleDailyAt ?? []).Select(t => TimeOnly.Parse(t, CultureInfo.InvariantCulture)).Order()]),
                NextRunAt = NextRunAt,
                CreateClasses = CreateClasses,
                SwitchOffMissing = SwitchOffMissing,
            },
            Version);
    }

    private sealed class LastRunRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset LastRunAt { get; init; }
        public string? LastRunBy { get; init; }
        public string? LastRunSummary { get; init; }
        public string? LastRunOutcome { get; init; }
    }

    private sealed class RunRow
    {
        public Guid Id { get; init; }
        public Guid ConnectionId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset FinishedAt { get; init; }
        public string Trigger { get; init; } = "";
        public string Outcome { get; init; } = "";
        public int Created { get; init; }
        public int Updated { get; init; }
        public int Unchanged { get; init; }
        public int Errors { get; init; }
        public string Summary { get; init; } = "";
        public string Actor { get; init; } = "";
    }
}
