using Dapper;
using Npgsql;
using Pulaski.Application.Labor;
using Pulaski.Application.MachineData;
using Pulaski.Domain.MachineData;
using Pulaski.Infrastructure.Labor;
using Pulaski.Infrastructure.Shifts;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Read side for the utilization and OEE report (D-052): the machine history of a period, as recorded.</summary>
internal sealed class OeeQueries(NpgsqlDataSource dataSource) : IOeeReadStore
{
    private const string TimeZone = "SELECT time_zone FROM core.equipment WHERE id = @Id AND level = 'site'";

    // Tree order; each row's parent is the nearest equipment above it that tracks state, as on the Machines board.
    private const string Equipment = """
        WITH RECURSIVE tree AS (
            SELECT e.id, ARRAY[e.code] AS path FROM core.equipment e WHERE e.id = @SiteId
            UNION ALL
            SELECT c.id, t.path || c.code FROM core.equipment c JOIN tree t ON c.parent_id = t.id
        )
        SELECT e.id, e.code, e.name, e.state_derivation, e.constraint_equipment_id, e.can_run_job,
               (SELECT c.ancestor_id FROM core.equipment_closure c JOIN core.equipment a ON a.id = c.ancestor_id
                WHERE c.descendant_id = e.id AND c.depth > 0 AND a.tracks_state ORDER BY c.depth LIMIT 1) AS parent_id
        FROM tree t
        JOIN core.equipment e ON e.id = t.id
        WHERE e.tracks_state AND e.is_active
        ORDER BY t.path
        """;

    // The site's own setting of a mode wins over the enterprise default (site_id NULL).
    private const string ProductionModes = """
        SELECT mode_number::int FROM (
            SELECT DISTINCT ON (mode_number) mode_number, is_production_time
            FROM core.unit_mode
            WHERE site_id = @SiteId OR site_id IS NULL
            ORDER BY mode_number, site_id NULLS LAST
        ) m
        WHERE is_production_time
        """;

    private const string States = """
        SELECT equipment_id, occurred_at AS at, packml_state::int AS pack_ml_state, unit_mode::int AS unit_mode
        FROM ts.equipment_state_change
        WHERE equipment_id = ANY(@Ids) AND occurred_at >= @From AND occurred_at < @To
        UNION ALL
        SELECT b.equipment_id, b.at, b.pack_ml_state, b.unit_mode
        FROM unnest(@Ids) AS i(id)
        CROSS JOIN LATERAL (
            SELECT equipment_id, occurred_at AS at, packml_state::int AS pack_ml_state, unit_mode::int AS unit_mode
            FROM ts.equipment_state_change
            WHERE equipment_id = i.id AND occurred_at < @From
            ORDER BY occurred_at DESC, recorded_at DESC
            LIMIT 1
        ) b
        """;

    // A stop that started more than 35 days before the period is left out: the machine still shows as down, just uncoded.
    private const string Stops = """
        SELECT d.equipment_id, d.started_at, d.ended_at, d.effective_reason_code_id AS reason_code_id, r.utilization_class, d.is_micro_stop
        FROM ts.downtime d
        LEFT JOIN core.reason_code r ON r.id = d.effective_reason_code_id
        WHERE d.equipment_id = ANY(@Ids) AND d.started_at < @To AND d.started_at >= @From - interval '35 days'
          AND (d.ended_at IS NULL OR d.ended_at > @From)
        """;

    // A changeover or maintenance job may have no material, and so no ideal rate.
    private const string Runs = """
        SELECT r.id AS run_id, r.equipment_id, r.started_at, r.ended_at, j.material_id, m.code AS material_code, m.name AS material_name,
               j.uom_id AS job_uom_id, cap.ideal_rate_per_hour, cap.rate_uom_id, j.job_number, j.counts_as_planned_downtime
        FROM core.job_run r
        JOIN core.job j ON j.id = r.job_id
        LEFT JOIN core.material m ON m.id = j.material_id
        LEFT JOIN LATERAL (
            SELECT c.ideal_rate_per_hour, c.rate_uom_id
            FROM core.equipment_capability c
            WHERE c.equipment_id = r.equipment_id AND c.material_id = j.material_id
              AND c.effective_from <= r.started_at AND (c.effective_to IS NULL OR c.effective_to > r.started_at)
            LIMIT 1
        ) cap ON true
        WHERE r.equipment_id = ANY(@Ids) AND r.started_at < @To AND (r.ended_at IS NULL OR r.ended_at > @From)
        """;

    // A report writes a processed row (good and scrap) and, with scrap, a defective row; corrections offset them.
    private const string Counts = """
        SELECT c.run_id, c.occurred_at, c.uom_id,
               coalesce(sum(c.delta_qty) FILTER (WHERE c.count_kind = 'processed'), 0) AS processed,
               coalesce(sum(c.delta_qty) FILTER (WHERE c.count_kind = 'defective'), 0) AS defective
        FROM ts.production_count c
        WHERE c.equipment_id = ANY(@Ids) AND c.occurred_at >= @From AND c.occurred_at < @To
          AND c.source <> 'machine' AND c.run_id IS NOT NULL AND c.count_kind IN ('processed', 'defective')
        GROUP BY c.run_id, c.occurred_at, c.uom_id
        """;

    private const string Reasons = "SELECT id, code, name, utilization_class FROM core.reason_code WHERE id = ANY(@Ids)";

    public async Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(TimeZone, new { Id = siteId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<OeeEquipmentRecord>> ListEquipmentAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EquipmentRow>(new CommandDefinition(Equipment, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new OeeEquipmentRecord(r.Id, r.Code, r.Name, r.ParentId, r.StateDerivation, r.ConstraintEquipmentId, r.CanRunJob))];
    }

    public async Task<IReadOnlySet<int>> GetProductionModesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var modes = (await connection.QueryAsync<int>(new CommandDefinition(ProductionModes, new { SiteId = siteId }, cancellationToken: cancellationToken))).ToHashSet();
        return modes.Count > 0 ? modes : new HashSet<int> { 1 };
    }

    public async Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<ShiftSql.CalendarRow>(new CommandDefinition(ShiftSql.EquipmentCalendars, new { Ids = equipmentIds.ToArray() }, cancellationToken: cancellationToken)))
            .Select(LaborSql.ToShifts)];
    }

    public async Task<IReadOnlyList<(Guid EquipmentId, StatePoint State)>> ListStatesAsync(
        IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0 || windowEnd <= windowStart)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<StateRow>(new CommandDefinition(States, new { Ids = equipmentIds.ToArray(), From = windowStart, To = windowEnd }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => (r.EquipmentId, new StatePoint(r.At, r.PackMlState, r.UnitMode)))];
    }

    public async Task<IReadOnlyList<(Guid EquipmentId, StopSpan Stop)>> ListStopsAsync(
        IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0 || windowEnd <= windowStart)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<StopRow>(new CommandDefinition(Stops, new { Ids = equipmentIds.ToArray(), From = windowStart, To = windowEnd }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => (r.EquipmentId, new StopSpan(r.StartedAt, r.EndedAt, r.ReasonCodeId, r.UtilizationClass, r.IsMicroStop)))];
    }

    public async Task<IReadOnlyList<OeeRunRecord>> ListRunsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0 || windowEnd <= windowStart)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(Runs, new { Ids = equipmentIds.ToArray(), From = windowStart, To = windowEnd }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new OeeRunRecord(r.RunId, r.EquipmentId, r.StartedAt, r.EndedAt, r.MaterialId, r.MaterialCode, r.MaterialName, r.JobUomId,
            r.IdealRatePerHour, r.RateUomId, r.JobNumber, r.CountsAsPlannedDowntime))];
    }

    public async Task<IReadOnlyList<OeeCountRecord>> ListCountsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0 || windowEnd <= windowStart)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<CountRow>(new CommandDefinition(Counts, new { Ids = equipmentIds.ToArray(), From = windowStart, To = windowEnd }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new OeeCountRecord(r.RunId, r.OccurredAt, r.UomId, r.Processed, r.Defective))];
    }

    public async Task<IReadOnlyList<OeeReasonRecord>> ListReasonsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReasonRow>(new CommandDefinition(Reasons, new { Ids = ids.ToArray() }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new OeeReasonRecord(r.Id, r.Code, r.Name, r.UtilizationClass))];
    }

    private sealed class EquipmentRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string StateDerivation { get; init; } = StateDerivations.OwnTags;
        public Guid? ConstraintEquipmentId { get; init; }
        public bool CanRunJob { get; init; }
        public Guid? ParentId { get; init; }
    }

    private sealed class StateRow
    {
        public Guid EquipmentId { get; init; }
        public DateTimeOffset At { get; init; }
        public int PackMlState { get; init; }
        public int UnitMode { get; init; }
    }

    private sealed class StopRow
    {
        public Guid EquipmentId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public Guid? ReasonCodeId { get; init; }
        public string? UtilizationClass { get; init; }
        public bool IsMicroStop { get; init; }
    }

    private sealed class RunRow
    {
        public Guid RunId { get; init; }
        public Guid EquipmentId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public Guid? MaterialId { get; init; }
        public string? MaterialCode { get; init; }
        public string? MaterialName { get; init; }
        public Guid? JobUomId { get; init; }
        public decimal? IdealRatePerHour { get; init; }
        public Guid? RateUomId { get; init; }
        public string JobNumber { get; init; } = "";
        public bool CountsAsPlannedDowntime { get; init; }
    }

    private sealed class CountRow
    {
        public Guid RunId { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public Guid? UomId { get; init; }
        public decimal Processed { get; init; }
        public decimal Defective { get; init; }
    }

    private sealed class ReasonRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? UtilizationClass { get; init; }
    }
}
