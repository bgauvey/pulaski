using System.Text.Json;
using Pulaski.Application.MachineData;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Write side for the daily OEE rollups (#66), in the rolling transaction.</summary>
internal sealed class OeeRollupRepository(DbSession session) : IOeeRollupRepository
{
    private const string SaveFigures = """
        INSERT INTO ts.oee_rollup (equipment_id, production_date, site_id, rolled_at, windows)
        SELECT u.equipment_id, @Day, @SiteId, @At, u.windows::jsonb
        FROM unnest(@Ids::uuid[], @Windows::text[]) AS u(equipment_id, windows)
        ON CONFLICT (equipment_id, production_date) DO UPDATE SET site_id = excluded.site_id, rolled_at = excluded.rolled_at, windows = excluded.windows
        """;

    // A mark made after it was read (or while rolling: the upsert waits for that transaction) keeps the day marked, so it rolls again.
    private const string SaveDay = """
        INSERT INTO ts.oee_rollup_day AS r (site_id, production_date, rolled_generation, rolled_at, dirty_at)
        VALUES (@SiteId, @Day, @Generation, @At, NULL)
        ON CONFLICT (site_id, production_date) DO UPDATE
        SET rolled_generation = excluded.rolled_generation, rolled_at = excluded.rolled_at,
            dirty_at = CASE WHEN r.dirty_at IS NOT DISTINCT FROM @SeenDirtyAt THEN NULL ELSE r.dirty_at END
        """;

    public async Task SaveDayAsync(OeeDayToRoll day, IReadOnlyCollection<Guid> equipmentIds, IReadOnlyList<OeeWindowFigures> figures, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var byEquipment = figures.ToLookup(f => f.EquipmentId);
        var ids = equipmentIds.ToArray();
        await session.ExecuteAsync(SaveFigures, new
        {
            Day = day.ProductionDate,
            day.SiteId,
            At = at,
            Ids = ids,
            Windows = ids.Select(id => JsonSerializer.Serialize(byEquipment[id].ToList(), OeeRollupQueries.Json)).ToArray(),
        }, cancellationToken);
        await session.ExecuteAsync(SaveDay, new { day.SiteId, Day = day.ProductionDate, day.Generation, At = at, SeenDirtyAt = day.DirtyAt }, cancellationToken);
    }
}
