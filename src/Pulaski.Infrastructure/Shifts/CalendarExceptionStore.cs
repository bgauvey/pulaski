using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Shifts;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Shifts;

/// <summary>Read side for calendar exceptions (D-043).</summary>
internal sealed class CalendarExceptionQueries(NpgsqlDataSource dataSource) : ICalendarExceptionReadStore
{
    private const string List = """
        SELECT x.id, e.site_id, x.scope_equipment_id, e.code AS scope_code, e.name AS scope_name, e.level AS scope_level,
               x.kind, x.starts_at, x.ends_at, x.note, x.is_active, x.xmin::text::bigint AS version
        FROM core.calendar_exception x
        JOIN core.equipment e ON e.id = x.scope_equipment_id
        WHERE e.site_id = @SiteId
          AND (@From::timestamptz IS NULL OR x.ends_at > @From)
          AND (x.is_active OR @IncludeRemoved)
        ORDER BY x.starts_at, e.code
        """;

    public async Task<IReadOnlyList<CalendarExceptionDto>> ListAsync(Guid siteId, DateTimeOffset? from, bool includeRemoved, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<CalendarExceptionDto>(new CommandDefinition(
            List, new { SiteId = siteId, From = from, IncludeRemoved = includeRemoved }, cancellationToken: cancellationToken))).AsList();
    }
}

/// <summary>Write side for calendar exceptions (D-043). Rows are never deleted: a removed one is switched off.</summary>
internal sealed class CalendarExceptionRepository(DbSession session) : ICalendarExceptionRepository
{
    private const string Get = """
        SELECT id, scope_equipment_id, kind, starts_at, ends_at, note, is_active, xmin::text::bigint AS version
        FROM core.calendar_exception
        WHERE id = @Id
        FOR UPDATE
        """;

    private const string Insert = """
        INSERT INTO core.calendar_exception (id, scope_equipment_id, kind, starts_at, ends_at, note, is_active, created_at, updated_at)
        VALUES (@Id, @ScopeEquipmentId, @Kind, @StartsAt, @EndsAt, @Note, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.calendar_exception
        SET kind = @Kind, starts_at = @StartsAt, ends_at = @EndsAt, note = @Note, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    public async Task<Versioned<CalendarExceptionRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Row>(Get, new { Id = id }, cancellationToken) is { } r
            ? new Versioned<CalendarExceptionRecord>(new CalendarExceptionRecord(r.Id, r.ScopeEquipmentId, r.Kind, r.StartsAt, r.EndsAt, r.Note, r.IsActive), r.Version)
            : null;

    public Task<long> InsertAsync(CalendarExceptionRecord exception, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Args(exception, at), cancellationToken);

    public Task<long> UpdateAsync(CalendarExceptionRecord exception, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, Args(exception, at), cancellationToken);

    private static object Args(CalendarExceptionRecord x, DateTimeOffset at) =>
        new { x.Id, x.ScopeEquipmentId, x.Kind, x.StartsAt, x.EndsAt, x.Note, x.IsActive, At = at };

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid ScopeEquipmentId { get; init; }
        public string Kind { get; init; } = "";
        public DateTimeOffset StartsAt { get; init; }
        public DateTimeOffset EndsAt { get; init; }
        public string? Note { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }
}
