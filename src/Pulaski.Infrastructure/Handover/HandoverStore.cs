using Dapper;
using Npgsql;
using Pulaski.Application.Handover;
using Pulaski.Contracts.Handover;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Handover;

/// <summary>Handover writes (#124, D-113), in the command's transaction.</summary>
internal sealed class HandoverRepository(DbSession session) : IHandoverRepository
{
    public Task<HandoverLine?> GetLineAsync(Guid lineId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<HandoverLine>(
            "SELECT id, site_id, code, can_run_job, is_active FROM core.equipment WHERE id = @Id AND site_id IS NOT NULL", new { Id = lineId }, cancellationToken);

    public Task<HandoverEntry?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<HandoverEntry>(
            """
            SELECT id, site_id, line_id, kind, body, production_date, shift_code, written_at, written_by, closed_at, closed_by, closing_note
            FROM core.handover_entry WHERE id = @Id FOR UPDATE
            """,
            new { Id = id }, cancellationToken);

    public Task InsertAsync(HandoverEntry entry, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.handover_entry (id, site_id, line_id, production_date, shift_code, kind, body, written_at, written_by, created_at, updated_at)
            VALUES (@Id, @SiteId, @LineId, @ProductionDate, @ShiftCode, @Kind, @Body, @WrittenAt, @WrittenBy, @WrittenAt, @WrittenAt)
            """,
            new { entry.Id, entry.SiteId, entry.LineId, entry.ProductionDate, entry.ShiftCode, entry.Kind, entry.Body, entry.WrittenAt, entry.WrittenBy },
            cancellationToken);

    public Task CloseAsync(HandoverEntry entry, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "UPDATE core.handover_entry SET closed_at = @ClosedAt, closed_by = @ClosedBy, closing_note = @ClosingNote, updated_at = @ClosedAt WHERE id = @Id",
            new { entry.Id, entry.ClosedAt, entry.ClosedBy, entry.ClosingNote }, cancellationToken);
}

/// <summary>Handover reads (#124, D-113).</summary>
internal sealed class HandoverQueries(NpgsqlDataSource dataSource) : IHandoverReadStore
{
    private const string Lines = """
        SELECT id, code, name FROM core.equipment
        WHERE site_id = @SiteId AND can_run_job AND is_active AND (@LineId::uuid IS NULL OR id = @LineId)
        ORDER BY code
        """;

    // Who wrote and closed it, by the users' names where they are users (actor as on evt.event).
    private const string Entries = """
        SELECT h.id, h.line_id, h.kind, h.body, h.production_date, h.shift_code, h.written_at, h.written_by,
               (SELECT u.display_name FROM sec.app_user u
                WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = h.written_by) AS written_by_name,
               h.closed_at, h.closed_by,
               (SELECT u.display_name FROM sec.app_user u
                WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = h.closed_by) AS closed_by_name,
               h.closing_note
        FROM core.handover_entry h
        WHERE h.line_id = ANY(@LineIds)
          AND (h.production_date >= @From OR (h.kind = 'open_item' AND h.closed_at IS NULL))
        ORDER BY h.written_at
        """;

    public async Task<IReadOnlyList<(Guid Id, string Code, string? Name)>> ListLinesAsync(Guid siteId, Guid? lineId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<(Guid, string, string?)>(new CommandDefinition(Lines, new { SiteId = siteId, LineId = lineId }, cancellationToken: cancellationToken))];
    }

    public async Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT time_zone FROM core.equipment WHERE id = @Id", new { Id = siteId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<HandoverEntryDto>> ListEntriesAsync(IReadOnlyCollection<Guid> lineIds, DateOnly fromDate, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<HandoverEntryDto>(new CommandDefinition(
            Entries, new { LineIds = lineIds.ToArray(), From = fromDate }, cancellationToken: cancellationToken))];
    }
}
