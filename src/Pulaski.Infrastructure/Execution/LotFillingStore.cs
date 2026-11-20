using Pulaski.Application.Execution;
using Pulaski.Application.Labor;
using Pulaski.Domain.Inventory;
using Pulaski.Infrastructure.Labor;
using Pulaski.Infrastructure.Persistence;
using Pulaski.Infrastructure.Shifts;

namespace Pulaski.Infrastructure.Execution;

/// <summary>What makes a lot one a job can still fill (D-104), shared by the report's lock and the job page.</summary>
internal static class LotFillingSql
{
    /// <summary>
    /// For the lot <c>c</c>, the job <c>@JobId</c> and the line <c>@LineId</c>: available, loose, not closed, numbered, and untouched but for
    /// the job's production on the line and reversals of it.
    /// </summary>
    public const string Fillable = """
        c.status = 'available' AND c.container_id IS NULL AND c.produced_to IS NULL AND NOT c.is_bulk AND c.lot_number IS NOT NULL
        AND NOT EXISTS (
            SELECT 1 FROM ts.lot_event e
            LEFT JOIN core.job_run r ON r.id = e.run_id
            WHERE e.lot_id = c.id
              AND NOT (e.event_kind = 'produced' AND e.job_id = @JobId AND r.equipment_id = @LineId)
              AND NOT (e.reverses_event_id IS NOT NULL AND e.job_id = @JobId))
        """;

    /// <summary>
    /// The job <c>@JobId</c>'s latest lot at each location. Latest by label number: a sequence, so strictly in the order lots were made, where
    /// lots made by one report can share a time and UUIDv7 ids aren't ordered within a millisecond.
    /// </summary>
    public const string LatestPerLocation = """
        SELECT DISTINCT ON (l.location_equipment_id) l.*
        FROM core.lot l
        WHERE l.produced_by_job_id = @JobId
        ORDER BY l.location_equipment_id, l.label_no DESC
        """;

    /// <summary>When the lot <c>c</c>'s first production counted.</summary>
    public const string StartedAt =
        "(SELECT min(coalesce(e.effective_at, e.occurred_at)) FROM ts.lot_event e WHERE e.lot_id = c.id AND e.event_kind = 'produced')";

    /// <summary>The rule of the capability of <c>@LineId</c> for <c>@MaterialId</c> in force at <c>@At</c>.</summary>
    public const string Rule = """
        SELECT lot_size AS size, lot_hours AS hours, lot_by_shift AS by_shift
        FROM core.equipment_capability
        WHERE equipment_id = @LineId AND material_id = @MaterialId AND effective_from <= @At AND (effective_to IS NULL OR effective_to > @At)
        LIMIT 1
        """;

    internal sealed class RuleRow
    {
        public decimal? Size { get; init; }
        public decimal? Hours { get; init; }
        public bool ByShift { get; init; }
    }
}

/// <summary>Lots filled by reports (D-104): the line's rule, the lot being filled, and closing it; in the caller's transaction.</summary>
internal sealed class LotFillingStore(DbSession session) : ILotFillingStore
{
    // The job's latest lot at the location (by label number, see LatestPerLocation), locked before it is judged, so a move or hold committed
    // meanwhile is seen: the next statement reads after the lock.
    private const string LockLatest = """
        SELECT l.id FROM core.lot l
        WHERE l.produced_by_job_id = @JobId AND l.location_equipment_id = @LocationId
        ORDER BY l.label_no DESC
        LIMIT 1
        FOR UPDATE
        """;

    // That lot, if it can still be filled.
    private const string Open = $"""
        SELECT c.id AS lot_id, c.lot_number, c.quantity, {LotFillingSql.StartedAt} AS started_at
        FROM core.lot c
        WHERE c.id = @Id AND {LotFillingSql.Fillable}
        """;

    // Only the lots the job is filling: its latest at each location that can still be filled, as the job page shows them.
    private const string Close = $"""
        UPDATE core.lot SET produced_to = @At, updated_at = @At
        WHERE id IN (SELECT c.id FROM ({LotFillingSql.LatestPerLocation}) c WHERE {LotFillingSql.Fillable})
        RETURNING lot_number
        """;

    private const string LineOfJob = "SELECT equipment_id FROM core.job WHERE id = @JobId";

    public async Task<LotCutRule> GetRuleAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LotFillingSql.RuleRow>(LotFillingSql.Rule, new { LineId = lineId, MaterialId = materialId, At = at }, cancellationToken) is { } row
            ? new LotCutRule(row.Size, row.Hours, row.ByShift)
            : LotCutRule.Off;

    public async Task<OpenLot?> GetOpenLotForUpdateAsync(Guid jobId, Guid lineId, Guid locationId, CancellationToken cancellationToken)
    {
        var latest = await session.QuerySingleOrDefaultAsync<Guid?>(LockLatest, new { JobId = jobId, LocationId = locationId }, cancellationToken);
        if (latest is not { } id)
        {
            return null;
        }

        var row = await session.QuerySingleOrDefaultAsync<OpenRow>(Open, new { Id = id, JobId = jobId, LineId = lineId }, cancellationToken);
        return row is null ? null : new OpenLot(row.LotId, row.LotNumber, row.Quantity, row.StartedAt);
    }

    public async Task<EquipmentShifts?> GetShiftsAsync(Guid lineId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ShiftSql.CalendarRow>(ShiftSql.EquipmentCalendars, new { Ids = new[] { lineId } }, cancellationToken) is { } row
            ? LaborSql.ToShifts(row)
            : null;

    public async Task<IReadOnlyList<string>> CloseOpenLotsAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var lineId = await session.ExecuteScalarAsync<Guid>(LineOfJob, new { JobId = jobId }, cancellationToken);
        return [.. (await session.QueryAsync<string>(Close, new { JobId = jobId, LineId = lineId, At = at }, cancellationToken)).Order(StringComparer.Ordinal)];
    }

    private sealed class OpenRow
    {
        public Guid LotId { get; init; }
        public string LotNumber { get; init; } = "";
        public decimal Quantity { get; init; }
        public DateTimeOffset StartedAt { get; init; }
    }
}
