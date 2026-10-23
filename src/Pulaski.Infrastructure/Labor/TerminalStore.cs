using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Labor;

/// <summary>SQL shared by the terminal repository and read store (D-049).</summary>
internal static class TerminalSql
{
    public const string Columns = """
        t.id, t.site_id, t.code, t.name, t.scope_equipment_id, t.default_equipment_id, t.auth_mode, t.is_active, t.enrolled_at,
        t.xmin::text::bigint AS version
        """;

    public const string BadgeColumns = """
        b.id, b.person_id, b.badge_hash, b.pin_hash, b.issued_at, b.issued_by, b.revoked_at, b.revoked_by, b.failed_pins, b.locked_until, b.last_used_at
        """;

    internal sealed class Row
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public Guid ScopeEquipmentId { get; init; }
        public Guid? DefaultEquipmentId { get; init; }
        public string AuthMode { get; init; } = "";
        public bool IsActive { get; init; }
        public DateTimeOffset? EnrolledAt { get; init; }
        public long Version { get; init; }

        public TerminalRecord ToRecord() => new(Id, SiteId, Code, Name, ScopeEquipmentId, DefaultEquipmentId, AuthMode, IsActive, EnrolledAt);
    }

    internal sealed class BadgeRow
    {
        public Guid Id { get; init; }
        public Guid PersonId { get; init; }
        public byte[] BadgeHash { get; init; } = [];
        public string? PinHash { get; init; }
        public DateTimeOffset IssuedAt { get; init; }
        public string IssuedBy { get; init; } = "";
        public DateTimeOffset? RevokedAt { get; init; }
        public string? RevokedBy { get; init; }
        public int FailedPins { get; init; }
        public DateTimeOffset? LockedUntil { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }

        public BadgeRecord ToRecord() => new(Id, PersonId, BadgeHash, PinHash, IssuedAt, IssuedBy, RevokedAt, RevokedBy, FailedPins, LockedUntil, LastUsedAt);
    }
}

/// <summary>Write side for terminals and badges (D-049). Nothing is deleted: terminals are switched off, badges revoked.</summary>
internal sealed class TerminalRepository(DbSession session) : ITerminalRepository
{
    private const string Get = $"SELECT {TerminalSql.Columns} FROM sec.terminal t WHERE t.id = @Id FOR UPDATE";

    private const string CodeExists = "SELECT EXISTS (SELECT 1 FROM sec.terminal WHERE site_id = @SiteId AND code = @Code)";

    private const string Insert = """
        INSERT INTO sec.terminal (id, site_id, code, name, scope_equipment_id, default_equipment_id, auth_mode, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @ScopeEquipmentId, @DefaultEquipmentId, @AuthMode, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE sec.terminal
        SET name = @Name, scope_equipment_id = @ScopeEquipmentId, default_equipment_id = @DefaultEquipmentId, auth_mode = @AuthMode, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string SetCode = """
        UPDATE sec.terminal SET enrolment_code_hash = @Hash, enrolment_expires_at = @Expires, updated_at = @At WHERE id = @Id
        """;

    // The code is used up as it enrols: a second try with it finds nothing.
    private const string Enrol = $"""
        UPDATE sec.terminal t
        SET secret_hash = @Secret, enrolled_at = @Now, enrolment_code_hash = NULL, enrolment_expires_at = NULL, updated_at = @Now
        WHERE t.enrolment_code_hash = @Code AND t.enrolment_expires_at > @Now AND t.is_active
        RETURNING {TerminalSql.Columns}
        """;

    private const string Touch = "UPDATE sec.terminal SET last_seen_at = @At WHERE id = @Id";

    private const string GetBadge = $"SELECT {TerminalSql.BadgeColumns} FROM sec.badge b WHERE b.id = @Id FOR UPDATE";

    private const string ActiveOfPerson = $"SELECT {TerminalSql.BadgeColumns} FROM sec.badge b WHERE b.person_id = @PersonId AND b.revoked_at IS NULL FOR UPDATE";

    private const string ActiveByHash = $"SELECT {TerminalSql.BadgeColumns} FROM sec.badge b WHERE b.badge_hash = @Hash AND b.revoked_at IS NULL FOR UPDATE";

    private const string InsertBadge = """
        INSERT INTO sec.badge (id, person_id, badge_hash, pin_hash, issued_at, issued_by, failed_pins)
        VALUES (@Id, @PersonId, @BadgeHash, @PinHash, @IssuedAt, @IssuedBy, 0)
        """;

    private const string UpdateBadge = """
        UPDATE sec.badge
        SET revoked_at = @RevokedAt, revoked_by = @RevokedBy, failed_pins = @FailedPins, locked_until = @LockedUntil, last_used_at = @LastUsedAt
        WHERE id = @Id
        """;

    public async Task<Versioned<TerminalRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<TerminalSql.Row>(Get, new { Id = id }, cancellationToken) is { } r ? new(r.ToRecord(), r.Version) : null;

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CodeExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public Task<long> InsertAsync(TerminalRecord t, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Args(t, at), cancellationToken);

    public Task<long> UpdateAsync(TerminalRecord t, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, Args(t, at), cancellationToken);

    public Task SetEnrolmentCodeAsync(Guid id, byte[] codeHash, DateTimeOffset expiresAt, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(SetCode, new { Id = id, Hash = codeHash, Expires = expiresAt, At = at }, cancellationToken);

    public async Task<TerminalRecord?> EnrolAsync(byte[] codeHash, byte[] secretHash, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<TerminalSql.Row>(Enrol, new { Code = codeHash, Secret = secretHash, Now = now }, cancellationToken))?.ToRecord();

    public Task TouchAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken) => session.ExecuteAsync(Touch, new { Id = id, At = at }, cancellationToken);

    public async Task<BadgeRecord?> GetBadgeForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<TerminalSql.BadgeRow>(GetBadge, new { Id = id }, cancellationToken))?.ToRecord();

    public async Task<BadgeRecord?> GetActiveBadgeOfPersonForUpdateAsync(Guid personId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<TerminalSql.BadgeRow>(ActiveOfPerson, new { PersonId = personId }, cancellationToken))?.ToRecord();

    public async Task<BadgeRecord?> FindActiveBadgeForUpdateAsync(byte[] badgeHash, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<TerminalSql.BadgeRow>(ActiveByHash, new { Hash = badgeHash }, cancellationToken))?.ToRecord();

    public Task InsertBadgeAsync(BadgeRecord badge, CancellationToken cancellationToken) => session.ExecuteAsync(InsertBadge, badge, cancellationToken);

    public Task UpdateBadgeAsync(BadgeRecord badge, CancellationToken cancellationToken) => session.ExecuteAsync(UpdateBadge, badge, cancellationToken);

    private static object Args(TerminalRecord t, DateTimeOffset at) =>
        new { t.Id, t.SiteId, t.Code, t.Name, t.ScopeEquipmentId, t.DefaultEquipmentId, t.AuthMode, t.IsActive, At = at };
}

/// <summary>Read side for terminals and badges (D-049).</summary>
internal sealed class TerminalQueries(NpgsqlDataSource dataSource) : ITerminalReadStore
{
    private const string List = """
        SELECT t.id, t.site_id, t.code, t.name, t.scope_equipment_id, s.code AS scope_code, t.default_equipment_id, d.code AS default_code,
               t.auth_mode, t.is_active, t.enrolled_at,
               CASE WHEN t.enrolment_expires_at > now() THEN t.enrolment_expires_at END AS enrolment_expires_at,
               t.last_seen_at, t.xmin::text::bigint AS version
        FROM sec.terminal t
        JOIN core.equipment s ON s.id = t.scope_equipment_id
        LEFT JOIN core.equipment d ON d.id = t.default_equipment_id
        WHERE t.site_id = @SiteId
        ORDER BY t.is_active DESC, t.code
        """;

    private const string BySecret = $"SELECT {TerminalSql.Columns} FROM sec.terminal t WHERE t.secret_hash = @Hash AND t.is_active AND t.enrolled_at IS NOT NULL";

    private const string Lines = """
        SELECT e.id, e.code, e.name, j.job_number AS running_job_number
        FROM sec.terminal t
        JOIN core.equipment_closure c ON c.ancestor_id = t.scope_equipment_id
        JOIN core.equipment e ON e.id = c.descendant_id AND e.can_run_job AND e.is_active
        LEFT JOIN core.job_run r ON r.equipment_id = e.id AND r.ended_at IS NULL
        LEFT JOIN core.job j ON j.id = r.job_id
        WHERE t.id = @Id
        ORDER BY (e.id = t.default_equipment_id) DESC, e.code
        """;

    private const string Badges = """
        SELECT b.id, b.person_id, p.display_name AS person_name, p.employee_number, (b.pin_hash IS NOT NULL) AS has_pin, b.issued_at, b.issued_by,
               b.revoked_at, b.revoked_by, CASE WHEN b.locked_until > now() THEN b.locked_until END AS locked_until, b.last_used_at
        FROM sec.badge b
        JOIN core.person p ON p.id = b.person_id
        WHERE p.site_id = @SiteId
        ORDER BY p.display_name, b.issued_at DESC
        """;

    public async Task<IReadOnlyList<TerminalDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<TerminalDto>(new CommandDefinition(List, new { SiteId = siteId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<TerminalRecord?> FindBySecretAsync(byte[] secretHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<TerminalSql.Row>(new CommandDefinition(BySecret, new { Hash = secretHash }, cancellationToken: cancellationToken)))?.ToRecord();
    }

    public async Task<IReadOnlyList<TerminalLineDto>> ListLinesAsync(Guid terminalId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<(Guid Id, string Code, string Name, string? RunningJobNumber)>(
                new CommandDefinition(Lines, new { Id = terminalId }, cancellationToken: cancellationToken)))
            .Select(r => new TerminalLineDto(r.Id, r.Code, r.Name, r.RunningJobNumber))];
    }

    public async Task<IReadOnlyList<BadgeDto>> ListBadgesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<BadgeDto>(new CommandDefinition(Badges, new { SiteId = siteId }, cancellationToken: cancellationToken))).AsList();
    }
}
