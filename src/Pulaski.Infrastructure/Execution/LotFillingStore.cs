using Pulaski.Application.Execution;
using Pulaski.Application.Labor;
using Pulaski.Domain.Inventory;
using Pulaski.Infrastructure.Labor;
using Pulaski.Infrastructure.Persistence;
using Pulaski.Infrastructure.Shifts;

namespace Pulaski.Infrastructure.Execution;

/// <summary>Lots filled by reports (D-104): the line's rule, the lot being filled, and closing it; in the caller's transaction.</summary>
internal sealed class LotFillingStore(DbSession session) : ILotFillingStore
{
    private const string Rule = """
        SELECT lot_size AS size, lot_hours AS hours, lot_by_shift AS by_shift
        FROM core.equipment_capability
        WHERE equipment_id = @LineId AND material_id = @MaterialId AND effective_from <= @At AND (effective_to IS NULL OR effective_to > @At)
        LIMIT 1
        """;

    // The job's latest lot at the location, if it is still only the job's production on this line (and reversals of it), locked.
    private const string Open = """
        WITH latest AS (
            SELECT l.id, l.lot_number, l.quantity, l.status, l.container_id, l.produced_to, l.is_bulk
            FROM core.lot l
            WHERE l.produced_by_job_id = @JobId AND l.location_equipment_id = @LocationId
            ORDER BY l.created_at DESC, l.id DESC
            LIMIT 1)
        SELECT c.id AS lot_id, c.lot_number, c.quantity,
               (SELECT min(coalesce(e.effective_at, e.occurred_at)) FROM ts.lot_event e WHERE e.lot_id = c.id AND e.event_kind = 'produced') AS started_at
        FROM latest c
        WHERE c.status = 'available' AND c.container_id IS NULL AND c.produced_to IS NULL AND NOT c.is_bulk AND c.lot_number IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM ts.lot_event e
              LEFT JOIN core.job_run r ON r.id = e.run_id
              WHERE e.lot_id = c.id
                AND NOT (e.event_kind = 'produced' AND e.job_id = @JobId AND r.equipment_id = @LineId)
                AND NOT (e.reverses_event_id IS NOT NULL AND e.job_id = @JobId))
        """;

    private const string Lock = "SELECT id FROM core.lot WHERE id = @Id FOR UPDATE";

    private const string Close = """
        UPDATE core.lot SET produced_to = @At, updated_at = @At
        WHERE produced_by_job_id = @JobId AND produced_to IS NULL AND NOT is_bulk AND lot_number IS NOT NULL AND status = 'available'
        RETURNING lot_number
        """;

    public async Task<LotCutRule> GetRuleAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<RuleRow>(Rule, new { LineId = lineId, MaterialId = materialId, At = at }, cancellationToken) is { } row
            ? new LotCutRule(row.Size, row.Hours, row.ByShift)
            : LotCutRule.Off;

    public async Task<OpenLot?> GetOpenLotForUpdateAsync(Guid jobId, Guid lineId, Guid locationId, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<OpenRow>(Open, new { JobId = jobId, LineId = lineId, LocationId = locationId }, cancellationToken);
        if (row is null)
        {
            return null;
        }

        await session.ExecuteScalarAsync<Guid>(Lock, new { Id = row.LotId }, cancellationToken);
        return new OpenLot(row.LotId, row.LotNumber, row.Quantity, row.StartedAt);
    }

    public async Task<EquipmentShifts?> GetShiftsAsync(Guid lineId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ShiftSql.CalendarRow>(ShiftSql.EquipmentCalendars, new { Ids = new[] { lineId } }, cancellationToken) is { } row
            ? LaborSql.ToShifts(row)
            : null;

    public async Task<IReadOnlyList<string>> CloseOpenLotsAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<string>(Close, new { JobId = jobId, At = at }, cancellationToken)).Order(StringComparer.Ordinal)];

    private sealed class RuleRow
    {
        public decimal? Size { get; init; }
        public decimal? Hours { get; init; }
        public bool ByShift { get; init; }
    }

    private sealed class OpenRow
    {
        public Guid LotId { get; init; }
        public string LotNumber { get; init; } = "";
        public decimal Quantity { get; init; }
        public DateTimeOffset StartedAt { get; init; }
    }
}
