using Dapper;
using Npgsql;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Read side for machine status and stops (D-050).</summary>
internal sealed class StopQueries(NpgsqlDataSource dataSource) : IStopReadStore
{
    // Tree order; each row's parent is the nearest equipment above it that tracks state.
    private const string StatusSql = """
        WITH RECURSIVE tree AS (
            SELECT e.id, ARRAY[e.code] AS path FROM core.equipment e WHERE e.id = @SiteId
            UNION ALL
            SELECT c.id, t.path || c.code FROM core.equipment c JOIN tree t ON c.parent_id = t.id
        )
        SELECT e.id AS equipment_id, e.code, e.name, e.equipment_role, e.state_derivation, e.constraint_equipment_id,
               (SELECT c.ancestor_id FROM core.equipment_closure c JOIN core.equipment a ON a.id = c.ancestor_id
                WHERE c.descendant_id = e.id AND c.depth > 0 AND a.tracks_state ORDER BY c.depth LIMIT 1) AS parent_id,
               l.packml_state::int AS pack_ml_state, l.raw_state, l.unit_mode::int AS unit_mode, l.state_since, l.stop_reason AS stop_code,
               pc.message AS stop_code_text, l.machine_speed, l.signal_at,
               coalesce(oc.category, '') AS site_category,
               d.id AS stop_id, d.started_at AS stop_started_at, r.code AS stop_reason_code, r.name AS stop_reason_name,
               d.operator_reason_code_id IS NOT NULL AS stop_coded,
               j.job_number, m.code AS material_code,
               (SELECT sum(CASE pcnt.count_kind WHEN 'processed' THEN pcnt.delta_qty ELSE -pcnt.delta_qty END)
                FROM ts.production_count pcnt
                WHERE pcnt.run_id = run.id AND pcnt.source = 'machine' AND pcnt.count_kind IN ('processed', 'defective')
                  AND pcnt.occurred_at >= run.started_at) AS run_good_count,
               n.code AS edge_node_code, n.last_seen_at AS edge_seen_at,
               (SELECT count(*)::int FROM ts.downtime u
                WHERE u.equipment_id = e.id AND u.started_at > @Now - interval '1 day' AND u.effective_reason_code_id IS NULL AND NOT u.is_micro_stop
                  AND u.superseded_at IS NULL
                  AND (u.ended_at IS NOT NULL OR u.started_at < @Now - e.micro_stop_threshold)) AS uncoded_stops
        FROM tree t
        JOIN core.equipment e ON e.id = t.id
        LEFT JOIN core.equipment_live_state l ON l.equipment_id = e.id
        LEFT JOIN core.oee_state_category oc ON oc.site_id = @SiteId AND oc.packml_state = l.packml_state
        LEFT JOIN core.machine_profile_code pc ON pc.profile_id = e.machine_profile_id AND pc.code_kind = 'stop_reason' AND pc.raw_value = l.stop_reason
        LEFT JOIN ts.downtime d ON d.id = l.open_downtime_id AND d.started_at = l.open_downtime_started_at
        LEFT JOIN core.reason_code r ON r.id = d.effective_reason_code_id
        LEFT JOIN core.job_run run ON run.equipment_id = e.id AND run.ended_at IS NULL
        LEFT JOIN core.job j ON j.id = run.job_id
        LEFT JOIN core.material m ON m.id = j.material_id
        LEFT JOIN core.edge_node n ON n.id = l.edge_node_id
        WHERE e.tracks_state AND e.is_active
        ORDER BY t.path
        """;

    private const string StopsSql = """
        SELECT d.id, d.started_at, d.ended_at,
               extract(epoch FROM coalesce(d.ended_at, @Now) - d.started_at)::int AS duration_seconds,
               d.equipment_id, e.code AS equipment_code, d.packml_state::int AS pack_ml_state, d.unit_mode::int AS unit_mode,
               d.plc_stop_reason AS stop_code, pc.message AS stop_code_text,
               d.mapped_reason_code_id AS mapped_reason_id, mr.code AS mapped_reason_code, mr.name AS mapped_reason_name,
               d.operator_reason_code_id AS operator_reason_id, orc.code AS operator_reason_code, orc.name AS operator_reason_name,
               er.utilization_class, d.is_micro_stop, d.coded_by, d.coded_at, d.comment, j.job_number,
               d.source, d.replaces_ids, d.adjusted_by, d.adjusted_at, d.adjustment_comment, d.superseded_at, d.superseded_by, d.superseded_note
        FROM ts.downtime d
        JOIN core.equipment e ON e.id = d.equipment_id
        LEFT JOIN core.machine_profile_code pc ON pc.profile_id = e.machine_profile_id AND pc.code_kind = 'stop_reason' AND pc.raw_value = d.plc_stop_reason
        LEFT JOIN core.reason_code mr ON mr.id = d.mapped_reason_code_id
        LEFT JOIN core.reason_code orc ON orc.id = d.operator_reason_code_id
        LEFT JOIN core.reason_code er ON er.id = d.effective_reason_code_id
        LEFT JOIN core.job_run run ON run.id = d.run_id
        LEFT JOIN core.job j ON j.id = run.job_id
        WHERE e.site_id = @SiteId AND d.started_at >= @From AND d.started_at < @To
          AND (@EquipmentId::uuid IS NULL OR d.equipment_id = @EquipmentId
               OR d.equipment_id IN (SELECT descendant_id FROM core.equipment_closure WHERE ancestor_id = @EquipmentId))
          AND (@IncludeMicro OR NOT d.is_micro_stop)
          AND (@IncludeSuperseded OR d.superseded_at IS NULL)
          AND (NOT @UncodedOnly OR (d.effective_reason_code_id IS NULL AND NOT d.is_micro_stop AND d.superseded_at IS NULL
                                    AND (d.ended_at IS NOT NULL OR d.started_at < @Now - e.micro_stop_threshold)))
          -- Keyset paging (#10): after the last stop of the previous page, in the same total order.
          AND (@AfterStart::timestamptz IS NULL
               OR (d.started_at, d.superseded_at IS NULL, d.id) < (@AfterStart::timestamptz, @AfterCurrent::boolean, @AfterId::uuid))
        ORDER BY d.started_at DESC, (d.superseded_at IS NULL) DESC, d.id DESC
        LIMIT @Take
        """;

    public async Task<IReadOnlyList<MachineStatusRow>> ListMachineStatusAsync(Guid siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<StatusRow>(new CommandDefinition(StatusSql, new { SiteId = siteId, Now = now }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new MachineStatusRow(new MachineStatusDto
        {
            EquipmentId = r.EquipmentId,
            Code = r.Code,
            Name = r.Name,
            ParentId = r.ParentId,
            EquipmentRole = r.EquipmentRole,
            StateDerivation = r.StateDerivation,
            Category = r.PackMlState is { } state ? (r.SiteCategory.Length > 0 ? r.SiteCategory : OeeCategories.Default(state)) : null,
            PackMlState = r.PackMlState,
            PackMlName = r.PackMlState is { } s ? PackMlStates.Name(s) : null,
            RawState = r.RawState,
            UnitMode = r.UnitMode,
            StateSince = r.StateSince,
            StopCode = r.StopCode,
            StopCodeText = r.StopCodeText,
            MachineSpeed = r.MachineSpeed,
            SignalAt = r.SignalAt,
            OpenStop = r.StopId is { } stopId
                ? new MachineOpenStopDto { Id = stopId, StartedAt = r.StopStartedAt!.Value, ReasonCode = r.StopReasonCode, ReasonName = r.StopReasonName, IsCoded = r.StopCoded }
                : null,
            JobNumber = r.JobNumber,
            MaterialCode = r.MaterialCode,
            RunGoodCount = r.RunGoodCount,
            EdgeNodeCode = r.EdgeNodeCode,
            EdgeQuiet = r.EdgeNodeCode is not null && (r.EdgeSeenAt is not { } seen || now - seen > TimeSpan.FromMinutes(2)),
            UncodedStops = r.UncodedStops,
        }, r.ConstraintEquipmentId))];
    }

    public async Task<IReadOnlyList<DowntimeDto>> ListStopsAsync(StopFilter filter, StopCursor? after, int take, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<StopRow>(new CommandDefinition(StopsSql, new
        {
            filter.SiteId,
            filter.EquipmentId,
            filter.From,
            filter.To,
            filter.UncodedOnly,
            IncludeMicro = filter.IncludeMicroStops,
            filter.IncludeSuperseded,
            Now = now,
            AfterStart = after?.StartedAt,
            AfterCurrent = after?.IsCurrent,
            AfterId = after?.Id,
            Take = take,
        }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToDto())];
    }

    public async Task<IReadOnlyList<ReasonCodeDto>> ListCodingReasonsAsync(Guid equipmentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<ReasonCodeDto>(new CommandDefinition(StopSql.CodingReasons, new { Id = equipmentId }, cancellationToken: cancellationToken))).AsList();
    }

    private sealed class StatusRow
    {
        public Guid EquipmentId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string EquipmentRole { get; init; } = "";
        public string StateDerivation { get; init; } = "own_tags";
        public Guid? ConstraintEquipmentId { get; init; }
        public Guid? ParentId { get; init; }
        public int? PackMlState { get; init; }
        public int? RawState { get; init; }
        public int? UnitMode { get; init; }
        public DateTimeOffset? StateSince { get; init; }
        public int? StopCode { get; init; }
        public string? StopCodeText { get; init; }
        public decimal? MachineSpeed { get; init; }
        public DateTimeOffset? SignalAt { get; init; }
        public string SiteCategory { get; init; } = "";
        public Guid? StopId { get; init; }
        public DateTimeOffset? StopStartedAt { get; init; }
        public string? StopReasonCode { get; init; }
        public string? StopReasonName { get; init; }
        public bool StopCoded { get; init; }
        public string? JobNumber { get; init; }
        public string? MaterialCode { get; init; }
        public decimal? RunGoodCount { get; init; }
        public string? EdgeNodeCode { get; init; }
        public DateTimeOffset? EdgeSeenAt { get; init; }
        public int UncodedStops { get; init; }
    }

    private sealed class StopRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public int DurationSeconds { get; init; }
        public Guid EquipmentId { get; init; }
        public string EquipmentCode { get; init; } = "";
        public int PackMlState { get; init; }
        public int UnitMode { get; init; }
        public int? StopCode { get; init; }
        public string? StopCodeText { get; init; }
        public Guid? MappedReasonId { get; init; }
        public string? MappedReasonCode { get; init; }
        public string? MappedReasonName { get; init; }
        public Guid? OperatorReasonId { get; init; }
        public string? OperatorReasonCode { get; init; }
        public string? OperatorReasonName { get; init; }
        public string? UtilizationClass { get; init; }
        public bool IsMicroStop { get; init; }
        public string? CodedBy { get; init; }
        public DateTimeOffset? CodedAt { get; init; }
        public string? Comment { get; init; }
        public string? JobNumber { get; init; }
        public string Source { get; init; } = StopSources.Machine;
        public Guid[]? ReplacesIds { get; init; }
        public string? AdjustedBy { get; init; }
        public DateTimeOffset? AdjustedAt { get; init; }
        public string? AdjustmentComment { get; init; }
        public DateTimeOffset? SupersededAt { get; init; }
        public string? SupersededBy { get; init; }
        public string? SupersededNote { get; init; }

        public DowntimeDto ToDto() => new()
        {
            Id = Id,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            DurationSeconds = DurationSeconds,
            EquipmentId = EquipmentId,
            EquipmentCode = EquipmentCode,
            PackMlState = PackMlState,
            PackMlName = PackMlStates.Name(PackMlState),
            UnitMode = UnitMode,
            StopCode = StopCode,
            StopCodeText = StopCodeText,
            MappedReasonId = MappedReasonId,
            MappedReasonCode = MappedReasonCode,
            MappedReasonName = MappedReasonName,
            OperatorReasonId = OperatorReasonId,
            OperatorReasonCode = OperatorReasonCode,
            OperatorReasonName = OperatorReasonName,
            UtilizationClass = UtilizationClass,
            IsMicroStop = IsMicroStop,
            CodedBy = CodedBy,
            CodedAt = CodedAt,
            Comment = Comment,
            JobNumber = JobNumber,
            Source = Source,
            ReplacesIds = ReplacesIds ?? [],
            AdjustedBy = AdjustedBy,
            AdjustedAt = AdjustedAt,
            AdjustmentComment = AdjustmentComment,
            SupersededAt = SupersededAt,
            SupersededBy = SupersededBy,
            SupersededNote = SupersededNote,
        };
    }
}

internal static class StopSql
{
    // With reasons ticked on the machine's profile: those, and those its codes, states and this machine's overrides map to.
    // With none ticked (or no profile): every active utilization reason of the site. Active, selectable reasons of every site or the equipment's.
    public const string CodingReasons = """
        WITH eq AS (
            SELECT e.id, CASE WHEN e.level = 'site' THEN e.id ELSE e.site_id END AS site_id, e.machine_profile_id FROM core.equipment e WHERE e.id = @Id
        ), offered AS (
            SELECT pr.reason_code_id FROM eq JOIN core.machine_profile_reason pr ON pr.profile_id = eq.machine_profile_id AND pr.is_offered
        ), named AS (
            SELECT reason_code_id FROM offered
            UNION SELECT c.reason_code_id FROM eq JOIN core.machine_profile_code c ON c.profile_id = eq.machine_profile_id AND c.is_active AND c.reason_code_id IS NOT NULL
            UNION SELECT s.reason_code_id FROM eq JOIN core.machine_profile_state s ON s.profile_id = eq.machine_profile_id AND s.is_active AND s.reason_code_id IS NOT NULL
            UNION SELECT o.reason_code_id FROM eq JOIN core.equipment_stop_reason_map o ON o.equipment_id = eq.id AND o.is_active AND o.reason_code_id IS NOT NULL
        )
        SELECT r.id, r.reason_kind AS kind, r.code, r.name, r.requires_comment, r.utilization_class, t.group_path
        FROM core.reason_code r
        JOIN core.reason_tree() t ON t.id = r.id
        CROSS JOIN eq
        WHERE r.reason_kind = 'utilization' AND r.is_active AND r.is_selectable AND (r.site_id IS NULL OR r.site_id = eq.site_id)
          AND (NOT EXISTS (SELECT 1 FROM offered) OR r.id IN (SELECT reason_code_id FROM named))
        ORDER BY CASE r.utilization_class WHEN 'downtime' THEN 0 WHEN 'planned' THEN 1 ELSE 2 END, t.sort_key
        """;
}

/// <summary>Write side for coding stops (D-050).</summary>
internal sealed class StopRepository(Persistence.DbSession session) : IStopRepository
{
    private const string GetStop = """
        SELECT d.id, d.started_at, d.ended_at, d.equipment_id, e.code AS equipment_code, d.mapped_reason_code_id, d.operator_reason_code_id,
               d.is_micro_stop, d.comment, d.superseded_at IS NOT NULL AS is_superseded
        FROM ts.downtime d JOIN core.equipment e ON e.id = d.equipment_id
        WHERE d.id = @Id AND d.started_at = @StartedAt
        FOR UPDATE OF d
        """;

    private const string Code = """
        UPDATE ts.downtime
        SET operator_reason_code_id = @ReasonCodeId, comment = @Comment, coded_by = @CodedBy, coded_by_user_id = @CodedByUserId, coded_at = @At
        WHERE id = @Id AND started_at = @StartedAt
        """;

    public async Task<StopRecord?> GetStopForUpdateAsync(Guid id, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Row>(GetStop, new { Id = id, StartedAt = startedAt }, cancellationToken) is { } r
            ? new StopRecord(r.Id, r.StartedAt, r.EndedAt, r.EquipmentId, r.EquipmentCode, r.MappedReasonCodeId, r.OperatorReasonCodeId, r.IsMicroStop, r.Comment,
                r.IsSuperseded)
            : null;

    public async Task<IReadOnlyList<CodingReason>> ListCodingReasonsAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<ReasonCodeDto>(StopSql.CodingReasons, new { Id = equipmentId }, cancellationToken)).Select(r => new CodingReason(r.Id, r.RequiresComment))];

    public Task CodeStopAsync(Guid id, DateTimeOffset startedAt, Guid reasonCodeId, string? comment, string codedBy, Guid? codedByUserId, DateTimeOffset at,
        CancellationToken cancellationToken) =>
        session.ExecuteAsync(Code, new { Id = id, StartedAt = startedAt, ReasonCodeId = reasonCodeId, Comment = comment, CodedBy = codedBy, CodedByUserId = codedByUserId, At = at },
            cancellationToken);

    private sealed class Row
    {
        public Guid Id { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public Guid EquipmentId { get; init; }
        public string EquipmentCode { get; init; } = "";
        public Guid? MappedReasonCodeId { get; init; }
        public Guid? OperatorReasonCodeId { get; init; }
        public bool IsMicroStop { get; init; }
        public string? Comment { get; init; }
        public bool IsSuperseded { get; init; }
    }
}
