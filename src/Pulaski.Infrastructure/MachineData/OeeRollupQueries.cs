using System.Text.Json;
using Dapper;
using Npgsql;
using Pulaski.Application.MachineData;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Read side for the daily OEE rollups (#66).</summary>
internal sealed class OeeRollupQueries(NpgsqlDataSource dataSource) : IOeeRollupStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string CurrentDays = """
        SELECT r.production_date FROM ts.oee_rollup_day r
        LEFT JOIN ts.oee_rollup_site g ON g.site_id = r.site_id
        WHERE r.site_id = @SiteId AND r.production_date BETWEEN @From AND @To
          AND r.rolled_at IS NOT NULL AND r.dirty_at IS NULL AND r.rolled_generation = coalesce(g.generation, 0)
        """;

    private const string Figures = """
        SELECT windows::text FROM ts.oee_rollup
        WHERE equipment_id = ANY(@Ids) AND production_date = ANY(@Days)
        """;

    // From the day the history can begin (35 days before the site was made, as far as a stop is looked back for) or the oldest day kept,
    // to the day before yesterday: the last day whose shifts, and the output reported after them, are all in.
    private const string DaysToRoll = """
        SELECT s.id AS site_id, gs.at::date AS production_date, coalesce(g.generation, 0) AS generation, r.dirty_at
        FROM core.equipment s
        LEFT JOIN ts.oee_rollup_site g ON g.site_id = s.id
        CROSS JOIN LATERAL (SELECT (@Now AT TIME ZONE s.time_zone)::date AS today) t
        CROSS JOIN LATERAL generate_series(
            greatest(t.today - (@KeepDays - 1), (s.created_at AT TIME ZONE s.time_zone)::date - 35)::timestamp,
            (t.today - 2)::timestamp, interval '1 day') AS gs(at)
        LEFT JOIN ts.oee_rollup_day r ON r.site_id = s.id AND r.production_date = gs.at::date
        WHERE s.level = 'site' AND s.is_active AND (@SiteId::uuid IS NULL OR s.id = @SiteId)
          AND EXISTS (SELECT 1 FROM core.equipment e WHERE e.site_id = s.id AND e.tracks_state AND e.is_active)
          AND (r.rolled_at IS NULL OR r.dirty_at IS NOT NULL OR r.rolled_generation IS DISTINCT FROM coalesce(g.generation, 0))
        ORDER BY gs.at DESC, s.id
        LIMIT @Limit
        """;

    public async Task<IReadOnlySet<DateOnly>> ListCurrentDaysAsync(Guid siteId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<DateOnly>(new CommandDefinition(CurrentDays, new { SiteId = siteId, From = firstDay, To = lastDay }, cancellationToken: cancellationToken)))
            .ToHashSet();
    }

    public async Task<IReadOnlyList<OeeWindowFigures>> ListFiguresAsync(IReadOnlyCollection<Guid> equipmentIds, IReadOnlyCollection<DateOnly> days, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0 || days.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(Figures, new { Ids = equipmentIds.ToArray(), Days = days.ToArray() }, cancellationToken: cancellationToken));
        return [.. rows.SelectMany(json => JsonSerializer.Deserialize<List<OeeWindowFigures>>(json, Json) ?? [])];
    }

    public async Task<IReadOnlyList<OeeDayToRoll>> ListDaysToRollAsync(DateTimeOffset now, int keepDays, int limit, Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<DayRow>(new CommandDefinition(DaysToRoll,
            new { Now = now, KeepDays = keepDays, Limit = limit, SiteId = siteId }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new OeeDayToRoll(r.SiteId, r.ProductionDate, r.Generation, r.DirtyAt))];
    }

    private sealed class DayRow
    {
        public Guid SiteId { get; init; }
        public DateOnly ProductionDate { get; init; }
        public long Generation { get; init; }
        public DateTimeOffset? DirtyAt { get; init; }
    }
}
