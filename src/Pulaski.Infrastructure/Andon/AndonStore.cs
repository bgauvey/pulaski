using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Andon;
using Pulaski.Contracts.Andon;
using Pulaski.Domain.Andon;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Andon;

/// <summary>Andon's writes (#125, D-108), in the command's transaction.</summary>
internal sealed class AndonRepository(DbSession session) : IAndonRepository
{
    private const string TypeColumns = "id, site_id, code, name, escalate_after_minutes, sort_order, is_active, xmin::text::bigint AS version";

    private const string CallColumns = """
        id, site_id, line_id, andon_type_id AS type_id, status, note, raised_at, raised_by, raised_from, acknowledged_at, acknowledged_by,
        resolved_at, resolved_by, resolution
        """;

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site')", new { Id = siteId }, cancellationToken);

    public Task<AndonLine?> LockLineAsync(Guid lineId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<AndonLine>(
            "SELECT id, site_id, code, can_run_job, is_active FROM core.equipment WHERE id = @Id FOR NO KEY UPDATE", new { Id = lineId }, cancellationToken);

    public async Task<Versioned<AndonType>?> GetTypeForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<TypeRow>($"SELECT {TypeColumns} FROM core.andon_type WHERE id = @Id FOR UPDATE", new { Id = id }, cancellationToken) is { } r
            ? new Versioned<AndonType>(r.ToDomain(), r.Version)
            : null;

    public Task<bool> TypeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM core.andon_type WHERE site_id = @SiteId AND code = @Code)", new { SiteId = siteId, Code = code }, cancellationToken);

    public Task<long> InsertTypeAsync(AndonType type, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            INSERT INTO core.andon_type (id, site_id, code, name, escalate_after_minutes, sort_order, is_active, created_at, updated_at)
            VALUES (@Id, @SiteId, @Code, @Name, @EscalateAfterMinutes, @SortOrder, @IsActive, @At, @At)
            RETURNING xmin::text::bigint
            """,
            new { type.Id, type.SiteId, type.Code, type.Name, type.EscalateAfterMinutes, type.SortOrder, type.IsActive, At = at },
            cancellationToken);

    public Task<long> UpdateTypeAsync(AndonType type, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            UPDATE core.andon_type SET name = @Name, escalate_after_minutes = @EscalateAfterMinutes, sort_order = @SortOrder, is_active = @IsActive, updated_at = @At
            WHERE id = @Id
            RETURNING xmin::text::bigint
            """,
            new { type.Id, type.Name, type.EscalateAfterMinutes, type.SortOrder, type.IsActive, At = at },
            cancellationToken);

    public Task<AndonCall?> FindOpenCallForUpdateAsync(Guid lineId, Guid typeId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<AndonCall>(
            $"SELECT {CallColumns} FROM core.andon_call WHERE line_id = @LineId AND andon_type_id = @TypeId AND status <> 'resolved' FOR UPDATE",
            new { LineId = lineId, TypeId = typeId }, cancellationToken);

    public Task<AndonCall?> GetCallForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<AndonCall>($"SELECT {CallColumns} FROM core.andon_call WHERE id = @Id FOR UPDATE", new { Id = id }, cancellationToken);

    public Task InsertCallAsync(AndonCall andonCall, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.andon_call (id, site_id, line_id, andon_type_id, status, note, raised_at, raised_by, raised_from, created_at, updated_at)
            VALUES (@Id, @SiteId, @LineId, @TypeId, @Status, @Note, @RaisedAt, @RaisedBy, @RaisedFrom, @At, @At)
            """,
            new { andonCall.Id, andonCall.SiteId, andonCall.LineId, andonCall.TypeId, andonCall.Status, andonCall.Note, andonCall.RaisedAt, andonCall.RaisedBy, andonCall.RaisedFrom, At = at },
            cancellationToken);

    public Task UpdateCallAsync(AndonCall andonCall, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            UPDATE core.andon_call
            SET status = @Status, acknowledged_at = @AcknowledgedAt, acknowledged_by = @AcknowledgedBy, resolved_at = @ResolvedAt, resolved_by = @ResolvedBy,
                resolution = @Resolution, updated_at = @At
            WHERE id = @Id
            """,
            new { andonCall.Id, andonCall.Status, andonCall.AcknowledgedAt, andonCall.AcknowledgedBy, andonCall.ResolvedAt, andonCall.ResolvedBy, andonCall.Resolution, At = at },
            cancellationToken);

    private sealed class TypeRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public int EscalateAfterMinutes { get; init; }
        public int SortOrder { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }

        public AndonType ToDomain() => new(Id, SiteId, Code, Name, EscalateAfterMinutes, SortOrder, IsActive);
    }
}

/// <summary>Andon's reads (#125, D-108).</summary>
internal sealed class AndonQueries(NpgsqlDataSource dataSource) : IAndonReadStore
{
    private const string Types = """
        SELECT id, site_id, code, name, escalate_after_minutes, sort_order, is_active, xmin::text::bigint AS version
        FROM core.andon_type
        WHERE site_id = @SiteId
        ORDER BY is_active DESC, sort_order, code
        """;

    private const string Calls = """
        SELECT c.id, c.site_id, c.line_id, l.code AS line_code, l.name AS line_name, c.andon_type_id AS type_id, t.code AS type_code, t.name AS type_name,
               t.escalate_after_minutes, c.status, c.note, c.raised_at, c.raised_by, c.raised_from, c.acknowledged_at, c.acknowledged_by,
               c.resolved_at, c.resolved_by, c.resolution,
               (SELECT u.display_name FROM sec.app_user u WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = c.raised_by) AS raised_by_name,
               (SELECT u.display_name FROM sec.app_user u WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = c.acknowledged_by) AS acknowledged_by_name,
               (SELECT u.display_name FROM sec.app_user u WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = c.resolved_by) AS resolved_by_name
        FROM core.andon_call c
        JOIN core.andon_type t ON t.id = c.andon_type_id
        JOIN core.equipment l ON l.id = c.line_id
        WHERE c.site_id = @SiteId
          AND (@LineId::uuid IS NULL OR c.line_id = @LineId)
          AND (CASE WHEN @OpenOnly THEN c.status <> 'resolved'
                    ELSE (@From::timestamptz IS NULL OR c.raised_at >= @From) AND (@To::timestamptz IS NULL OR c.raised_at < @To) END)
        ORDER BY c.raised_at DESC
        LIMIT 1000
        """;

    public async Task<IReadOnlyList<AndonTypeDto>> ListTypesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<AndonTypeDto>(new CommandDefinition(Types, new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<AndonCallDto>> ListCallsAsync(
        Guid siteId, bool openOnly, DateTimeOffset? from, DateTimeOffset? until, Guid? lineId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<AndonCallDto>(new CommandDefinition(
            Calls, new { SiteId = siteId, OpenOnly = openOnly, From = from, To = until, LineId = lineId }, cancellationToken: cancellationToken))];
    }
}
