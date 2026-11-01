using Dapper;
using Npgsql;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Feed points, their movements and shortfalls, for screens (#33).</summary>
internal sealed class FeedQueries(NpgsqlDataSource dataSource) : IFeedReadStore
{
    private const string FeedPointsSql = """
        SELECT f.id, f.site_id, f.code, f.name, line.id AS line_id, line.code AS line_code,
               EXISTS (SELECT 1 FROM core.equipment_tag t WHERE t.equipment_id = f.id AND t.tag_role = 'consumed_count' AND t.is_active) AS has_counter
        FROM core.equipment f
        LEFT JOIN LATERAL (
            SELECT a.id, a.code FROM core.equipment_closure c JOIN core.equipment a ON a.id = c.ancestor_id
            WHERE c.descendant_id = f.id AND c.depth > 0 AND a.can_run_job
            ORDER BY c.depth LIMIT 1) line ON true
        WHERE f.equipment_role = 'feed_point' AND f.holds_inventory AND f.is_active AND (@SiteId::uuid IS NULL OR f.site_id = @SiteId)
        ORDER BY line.code NULLS LAST, f.code
        """;

    private const string LotsSql = $"""
        SELECT {InventoryQueries.LotColumns}
        {InventoryQueries.LotFrom}
        WHERE l.id = ANY(@Ids)
        """;

    private const string ShortfallsSql = """
        SELECT s.id, s.site_id, s.equipment_id, e.code AS equipment_code, s.job_id, j.job_number, s.material_id, m.code AS material_code,
               s.lot_id, l.lot_number, s.quantity, u.code AS uom_code, s.source, s.source_ref, s.occurred_at, s.reason, s.status,
               s.resolved_lot_id, rl.lot_number AS resolved_lot_number, s.resolved_by, s.resolved_at, s.resolution_note, s.xmin::text::bigint AS version
        FROM core.feed_shortfall s
        JOIN core.equipment e ON e.id = s.equipment_id
        LEFT JOIN core.job j ON j.id = s.job_id
        LEFT JOIN core.material m ON m.id = s.material_id
        LEFT JOIN core.lot l ON l.id = s.lot_id
        LEFT JOIN core.lot rl ON rl.id = s.resolved_lot_id
        LEFT JOIN core.uom u ON u.id = s.uom_id
        """;

    private const string ListShortfallsSql = $"""
        {ShortfallsSql}
        WHERE s.status = @Status AND (@SiteId::uuid IS NULL OR s.site_id = @SiteId)
        ORDER BY s.occurred_at DESC, s.id DESC
        LIMIT 500
        """;

    private const string ShortfallSql = $"""
        {ShortfallsSql}
        WHERE s.id = @Id
        """;

    private const string FeedEventsSql = $"""
        SELECT e.lot_id, l.material_id, e.effective_at AS at, e.occurred_at AS recorded_at, e.event_kind AS kind, e.qty_delta, e.qty_after,
               e.from_location_id, e.to_location_id
        FROM ts.lot_event e
        JOIN core.lot l ON l.id = e.lot_id
        WHERE (e.to_location_id = ANY(@Ids) OR e.from_location_id = ANY(@Ids))
          AND e.reverses_event_id IS NULL AND {LotEventSql.NotReversed}
        ORDER BY e.effective_at, e.occurred_at, e.id
        """;

    public async Task<IReadOnlyList<FeedPointDto>> ListFeedPointsAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FeedPointDto>(new CommandDefinition(FeedPointsSql, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FeedEvent>> ListFeedEventsAsync(IReadOnlyCollection<Guid> feedPointIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FeedRepository.FeedEventRow>(
            new CommandDefinition(FeedEventsSql, new { Ids = feedPointIds.ToArray() }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<LotSummaryDto>> GetLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        if (lotIds.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LotSummaryDto>(new CommandDefinition(LotsSql, new { Ids = lotIds.ToArray() }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FeedShortfallDto>> ListShortfallsAsync(Guid? siteId, string status, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FeedShortfallDto>(
            new CommandDefinition(ListShortfallsSql, new { SiteId = siteId, Status = status }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<FeedShortfallDto?> GetShortfallAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<FeedShortfallDto>(new CommandDefinition(ShortfallSql, new { Id = id }, cancellationToken: cancellationToken));
    }
}
