using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>
/// Keeps the daily OEE rollups (#66) honest, inside the transaction of the change: a write to a day's history (states, stops, runs, reported
/// output) marks that site's production days to roll again, and a change to what decides how time counts (shifts, calendars, equipment
/// settings, reason classes, ideal rates, unit conversions) moves the site to a new generation, so every one of its days rolls again.
/// Only days already rollable are marked (up to the day before yesterday, site-local), so live machine data marks nothing.
/// </summary>
internal static class OeeRollupMarks
{
    // A window counting towards the day before may hold the time, so that day is marked too. DISTINCT: one row per day, as ON CONFLICT needs.
    private const string MarkDays = """
        INSERT INTO ts.oee_rollup_day AS r (site_id, production_date, dirty_at)
        SELECT DISTINCT s.id, g.at::date, now()
        FROM unnest(@Ids::uuid[], @Froms::timestamptz[], @Tos::timestamptz[]) AS m(equipment_id, from_at, to_at)
        JOIN core.equipment e ON e.id = m.equipment_id
        JOIN core.equipment s ON s.id = e.site_id
        CROSS JOIN LATERAL generate_series(
            ((m.from_at AT TIME ZONE s.time_zone)::date - 1)::timestamp,
            least((m.to_at AT TIME ZONE s.time_zone)::date, (now() AT TIME ZONE s.time_zone)::date - 2)::timestamp,
            interval '1 day') AS g(at)
        ON CONFLICT (site_id, production_date) DO UPDATE SET dirty_at = excluded.dirty_at
        """;

    private const string BumpSites = """
        INSERT INTO ts.oee_rollup_site AS g (site_id, generation, changed_at)
        SELECT s.id, 1, now() FROM core.equipment s
        WHERE s.level = 'site' AND (@All OR s.id = ANY(@SiteIds) OR s.id IN (SELECT site_id FROM core.equipment WHERE id = ANY(@EquipmentIds)))
        ON CONFLICT (site_id) DO UPDATE SET generation = g.generation + 1, changed_at = excluded.changed_at
        """;

    /// <summary>Marks the days each piece of equipment's history changed in, from <c>From</c> to <c>To</c>.</summary>
    public static Task MarkAsync(DbSession session, IEnumerable<(Guid EquipmentId, DateTimeOffset From, DateTimeOffset To)> changes, CancellationToken cancellationToken)
    {
        var list = changes.ToList();
        return list.Count == 0
            ? Task.CompletedTask
            : session.ExecuteAsync(MarkDays, new
            {
                Ids = list.Select(c => c.EquipmentId).ToArray(),
                Froms = list.Select(c => c.From).ToArray(),
                Tos = list.Select(c => c.To < c.From ? c.From : c.To).ToArray(),
            }, cancellationToken);
    }

    public static Task MarkAsync(DbSession session, Guid equipmentId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        MarkAsync(session, [(equipmentId, from, to)], cancellationToken);

    /// <summary>The settings of the equipment's site changed: every rolled day of it rolls again.</summary>
    public static Task SiteOfEquipmentChangedAsync(DbSession session, Guid equipmentId, CancellationToken cancellationToken) =>
        session.ExecuteAsync(BumpSites, new { All = false, SiteIds = Array.Empty<Guid>(), EquipmentIds = new[] { equipmentId } }, cancellationToken);

    /// <summary>A setting of one site changed, or of every site when <paramref name="siteId"/> is null (an enterprise-wide reason, say).</summary>
    public static Task SiteChangedAsync(DbSession session, Guid? siteId, CancellationToken cancellationToken) =>
        session.ExecuteAsync(BumpSites, new { All = siteId is null, SiteIds = siteId is { } id ? new[] { id } : [], EquipmentIds = Array.Empty<Guid>() }, cancellationToken);
}
