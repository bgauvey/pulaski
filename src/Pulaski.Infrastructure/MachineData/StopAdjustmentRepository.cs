using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Write side for adjusting stops (D-069): the equipment is locked as machine data ingest locks it, then its stops in id order.</summary>
internal sealed class StopAdjustmentRepository(Persistence.DbSession session) : IStopAdjustmentRepository
{
    private const string LockEquipment = "SELECT id, code, tracks_state FROM core.equipment WHERE id = @Id FOR NO KEY UPDATE";

    private const string EquipmentOfStops = """
        SELECT DISTINCT d.equipment_id FROM ts.downtime d
        WHERE (d.id, d.started_at) IN (SELECT * FROM unnest(@Ids::uuid[], @Starts::timestamptz[]))
        """;

    private const string GetStops = """
        SELECT d.id, d.started_at, d.ended_at, d.equipment_id, d.run_id, d.packml_state::int AS packml_state, d.unit_mode::int AS unit_mode,
               d.plc_stop_reason, d.mapped_reason_code_id, d.operator_reason_code_id, d.comment, d.coded_by, d.coded_by_user_id, d.coded_at,
               d.source, d.superseded_at IS NOT NULL AS is_superseded, d.is_micro_stop, d.replaces_ids, d.adjusted_at
        FROM ts.downtime d
        WHERE (d.id, d.started_at) IN (SELECT * FROM unnest(@Ids::uuid[], @Starts::timestamptz[]))
        ORDER BY d.id
        FOR UPDATE
        """;

    // The open stop is found through the live state however long ago it started; closed ones from 62 days back, twice the adjustable age.
    private const string CurrentStops = """
        SELECT d.id, d.started_at, d.ended_at FROM ts.downtime d
        WHERE d.equipment_id = @Id AND d.superseded_at IS NULL AND d.started_at < @To AND d.started_at >= @From - interval '62 days'
          AND (d.ended_at IS NULL OR d.ended_at > @From)
        UNION
        SELECT d.id, d.started_at, d.ended_at FROM core.equipment_live_state l
        JOIN ts.downtime d ON d.id = l.open_downtime_id AND d.started_at = l.open_downtime_started_at
        WHERE l.equipment_id = @Id AND d.started_at < @To
        """;

    private const string RunAt = """
        SELECT id FROM core.job_run WHERE equipment_id = @Id AND started_at <= @At AND (ended_at IS NULL OR ended_at > @At)
        ORDER BY started_at DESC LIMIT 1
        """;

    private const string Insert = """
        INSERT INTO ts.downtime (id, started_at, ended_at, equipment_id, run_id, packml_state, unit_mode, plc_stop_reason, mapped_reason_code_id,
                                 operator_reason_code_id, comment, coded_by, coded_by_user_id, coded_at, is_micro_stop, source, replaces_ids,
                                 adjusted_by, adjusted_by_user_id, adjusted_at, adjustment_comment)
        VALUES (@Id, @StartedAt, @EndedAt, @EquipmentId, @RunId, @PackMlState, @UnitMode, @PlcStopReason, @MappedReasonCodeId,
                @OperatorReasonCodeId, @Comment, @CodedBy, @CodedByUserId, @CodedAt, @IsMicroStop, @Source, @ReplacesIds,
                @AdjustedBy, @AdjustedByUserId, @AdjustedAt, @AdjustmentComment)
        """;

    private const string Supersede = """
        UPDATE ts.downtime SET superseded_at = @At, superseded_by = @By, superseded_note = @Note
        WHERE (id, started_at) IN (SELECT * FROM unnest(@Ids::uuid[], @Starts::timestamptz[])) AND superseded_at IS NULL
        """;

    public async Task<StopEquipment?> LockEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<EquipmentRow>(LockEquipment, new { Id = equipmentId }, cancellationToken) is { } r
            ? new StopEquipment(r.Id, r.Code, r.TracksState)
            : null;

    public async Task<IReadOnlyList<Guid>> EquipmentOfStopsAsync(IReadOnlyList<StopKeyDto> stops, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<Guid>(EquipmentOfStops, Keys(stops), cancellationToken)];

    public async Task<IReadOnlyList<AdjustableStop>> GetStopsForUpdateAsync(IReadOnlyList<StopKeyDto> stops, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<StopRow>(GetStops, Keys(stops), cancellationToken)).Select(ToStop)];

    public async Task<IReadOnlyList<StopInterval>> ListCurrentStopsAsync(Guid equipmentId, DateTimeOffset start, DateTimeOffset until, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<IntervalRow>(CurrentStops, new { Id = equipmentId, From = start, To = until }, cancellationToken))
            .Select(r => new StopInterval(r.Id, r.StartedAt, r.EndedAt))];

    public Task<Guid?> RunAtAsync(Guid equipmentId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(RunAt, new { Id = equipmentId, At = at }, cancellationToken);

    public Task InsertAsync(NewStop newStop, CancellationToken cancellationToken) =>
        session.ExecuteAsync(Insert, new
        {
            newStop.Id,
            newStop.StartedAt,
            newStop.EndedAt,
            newStop.EquipmentId,
            newStop.RunId,
            PackMlState = (short)newStop.PackMlState,
            UnitMode = (short)newStop.UnitMode,
            newStop.PlcStopReason,
            newStop.MappedReasonCodeId,
            newStop.OperatorReasonCodeId,
            newStop.Comment,
            newStop.CodedBy,
            newStop.CodedByUserId,
            newStop.CodedAt,
            newStop.IsMicroStop,
            newStop.Source,
            ReplacesIds = newStop.ReplacesIds?.ToArray(),
            newStop.AdjustedBy,
            newStop.AdjustedByUserId,
            newStop.AdjustedAt,
            newStop.AdjustmentComment,
        }, cancellationToken);

    public Task SupersedeAsync(IReadOnlyList<StopKeyDto> stops, string supersededBy, DateTimeOffset at, string? note, CancellationToken cancellationToken) =>
        session.ExecuteAsync(Supersede, new
        {
            Ids = stops.Select(s => s.Id).ToArray(),
            Starts = stops.Select(s => s.StartedAt).ToArray(),
            By = supersededBy,
            At = at,
            Note = note,
        }, cancellationToken);

    // One adjustment's stops: the same replaced stops, made at the same moment (both parts of a split). Locked, current or not.
    private const string Adjustment = """
        SELECT d.id, d.started_at, d.ended_at, d.equipment_id, d.run_id, d.packml_state::int AS packml_state, d.unit_mode::int AS unit_mode,
               d.plc_stop_reason, d.mapped_reason_code_id, d.operator_reason_code_id, d.comment, d.coded_by, d.coded_by_user_id, d.coded_at,
               d.source, d.superseded_at IS NOT NULL AS is_superseded, d.is_micro_stop, d.replaces_ids, d.adjusted_at
        FROM ts.downtime d
        WHERE d.equipment_id = @EquipmentId AND d.adjusted_at = @AdjustedAt AND d.replaces_ids @> @Ids AND d.replaces_ids <@ @Ids
          AND d.started_at >= @AdjustedAt - interval '62 days'
        ORDER BY d.id
        FOR UPDATE
        """;

    private const string Replaced = """
        SELECT d.id, d.started_at, d.ended_at, d.equipment_id, d.run_id, d.packml_state::int AS packml_state, d.unit_mode::int AS unit_mode,
               d.plc_stop_reason, d.mapped_reason_code_id, d.operator_reason_code_id, d.comment, d.coded_by, d.coded_by_user_id, d.coded_at,
               d.source, d.superseded_at IS NOT NULL AS is_superseded, d.is_micro_stop, d.replaces_ids, d.adjusted_at
        FROM ts.downtime d
        WHERE d.equipment_id = @EquipmentId AND d.id = ANY(@Ids) AND d.started_at >= @Near - interval '62 days'
        ORDER BY d.id
        FOR UPDATE
        """;

    public async Task<IReadOnlyList<AdjustableStop>> ListAdjustmentAsync(
        Guid equipmentId, IReadOnlyList<Guid> replacesIds, DateTimeOffset adjustedAt, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<StopRow>(Adjustment, new { EquipmentId = equipmentId, Ids = replacesIds.ToArray(), AdjustedAt = adjustedAt }, cancellationToken))
            .Select(ToStop)];

    public async Task<IReadOnlyList<AdjustableStop>> GetReplacedForUpdateAsync(Guid equipmentId, IReadOnlyList<Guid> ids, DateTimeOffset near, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<StopRow>(Replaced, new { EquipmentId = equipmentId, Ids = ids.ToArray(), Near = near }, cancellationToken)).Select(ToStop)];

    private static AdjustableStop ToStop(StopRow r) => new(
        r.Id, r.StartedAt, r.EndedAt, r.EquipmentId, r.RunId, r.PackMlState, r.UnitMode, r.PlcStopReason, r.MappedReasonCodeId, r.OperatorReasonCodeId,
        r.Comment, r.CodedBy, r.CodedByUserId, r.CodedAt, r.Source, r.IsSuperseded, r.IsMicroStop, r.ReplacesIds, r.AdjustedAt);

    private static object Keys(IReadOnlyList<StopKeyDto> stops) =>
        new { Ids = stops.Select(s => s.Id).ToArray(), Starts = stops.Select(s => s.StartedAt).ToArray() };

    private sealed class EquipmentRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public bool TracksState { get; init; }
    }

    private sealed class IntervalRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
    }

    private sealed class StopRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public Guid EquipmentId { get; init; }
        public Guid? RunId { get; init; }
        public int PackMlState { get; init; }
        public int UnitMode { get; init; }
        public int? PlcStopReason { get; init; }
        public Guid? MappedReasonCodeId { get; init; }
        public Guid? OperatorReasonCodeId { get; init; }
        public string? Comment { get; init; }
        public string? CodedBy { get; init; }
        public Guid? CodedByUserId { get; init; }
        public DateTimeOffset? CodedAt { get; init; }
        public string Source { get; init; } = StopSources.Machine;
        public bool IsSuperseded { get; init; }
        public bool IsMicroStop { get; init; }
        public Guid[]? ReplacesIds { get; init; }
        public DateTimeOffset? AdjustedAt { get; init; }
    }
}
