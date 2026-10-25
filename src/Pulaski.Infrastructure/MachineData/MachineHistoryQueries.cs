using Dapper;
using Npgsql;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Read side for alarms, warnings and process values (#26), as recorded.</summary>
internal sealed class MachineHistoryQueries(NpgsqlDataSource dataSource) : IMachineHistoryReadStore
{
    private const string AlarmsSql = """
        SELECT a.id, a.raised_at, a.cleared_at, extract(epoch FROM coalesce(a.cleared_at, @Now) - a.raised_at)::int AS duration_seconds,
               a.equipment_id, e.code AS equipment_code, a.alarm_kind AS kind, a.alarm_number AS number, a.message, t.source_address AS address
        FROM ts.equipment_alarm a
        JOIN core.equipment e ON e.id = a.equipment_id
        LEFT JOIN core.equipment_tag t ON t.id = a.equipment_tag_id
        WHERE e.site_id = @SiteId AND a.raised_at < @To AND (a.cleared_at IS NULL OR a.cleared_at > @From)
          AND (@EquipmentId::uuid IS NULL OR a.equipment_id = @EquipmentId
               OR a.equipment_id IN (SELECT descendant_id FROM core.equipment_closure WHERE ancestor_id = @EquipmentId))
          AND (NOT @ActiveOnly OR a.cleared_at IS NULL)
        ORDER BY (a.cleared_at IS NULL) DESC, a.raised_at DESC, a.id DESC
        LIMIT @Take
        """;

    private const string SeriesTagsSql = """
        SELECT t.id AS tag_id, t.equipment_id, e.code AS equipment_code, t.source_address AS address, u.code AS uom_code
        FROM core.equipment_tag t
        JOIN core.equipment e ON e.id = t.equipment_id
        LEFT JOIN core.uom u ON u.id = t.uom_id
        WHERE t.equipment_id = @EquipmentId AND t.tag_role IN ('process_value', 'level') AND (@TagId::uuid IS NULL OR t.id = @TagId)
        ORDER BY t.is_active DESC, t.source_address
        """;

    // Each tag's first readings in the period, oldest first: a tag with many readings doesn't crowd out the others.
    private const string PointsSql = """
        SELECT i.id AS tag_id, p.occurred_at AS at, p.value_num AS value, p.quality >= 192 AS good
        FROM unnest(@Ids::uuid[]) AS i(id)
        CROSS JOIN LATERAL (
            SELECT occurred_at, value_num, quality FROM ts.process_value
            WHERE equipment_tag_id = i.id AND occurred_at >= @From AND occurred_at < @To
            ORDER BY occurred_at
            LIMIT @Take
        ) p
        ORDER BY p.occurred_at
        """;

    public async Task<IReadOnlyList<AlarmDto>> ListAlarmsAsync(AlarmFilter filter, int take, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<AlarmDto>(new CommandDefinition(AlarmsSql, new
        {
            filter.SiteId, filter.EquipmentId, filter.From, filter.To, filter.ActiveOnly, Now = now, Take = take,
        }, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<ProcessValueSeriesDto>> ListProcessValuesAsync(
        Guid equipmentId, Guid? tagId, DateTimeOffset windowStart, DateTimeOffset windowEnd, int take, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var tags = (await connection.QueryAsync<SeriesTagRow>(new CommandDefinition(
            SeriesTagsSql, new { EquipmentId = equipmentId, TagId = tagId }, cancellationToken: cancellationToken))).ToList();
        if (tags.Count == 0)
        {
            return [];
        }

        var points = (await connection.QueryAsync<PointRow>(new CommandDefinition(
            PointsSql, new { Ids = tags.Select(t => t.TagId).ToArray(), From = windowStart, To = windowEnd, Take = take }, cancellationToken: cancellationToken)))
            .ToLookup(p => p.TagId);
        return [.. tags.Select(t => new ProcessValueSeriesDto
        {
            TagId = t.TagId,
            EquipmentId = t.EquipmentId,
            EquipmentCode = t.EquipmentCode,
            Address = t.Address,
            UomCode = t.UomCode,
            Points = [.. points[t.TagId].Select(p => new ProcessValuePointDto(p.At, p.Value, p.Good))],
        })];
    }

    private sealed class SeriesTagRow
    {
        public Guid TagId { get; init; }
        public Guid EquipmentId { get; init; }
        public string EquipmentCode { get; init; } = "";
        public string Address { get; init; } = "";
        public string? UomCode { get; init; }
    }

    private sealed class PointRow
    {
        public Guid TagId { get; init; }
        public DateTimeOffset At { get; init; }
        public double? Value { get; init; }
        public bool Good { get; init; }
    }
}
