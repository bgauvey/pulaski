using Dapper;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Domain.Scheduling;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Scheduling;

/// <summary>Write side for scheduling. Every statement runs in the command's transaction.</summary>
internal sealed class SchedulingRepository(DbSession session, IIdGenerator ids) : ISchedulingRepository
{
    private const string OpenStatuses = "('planned','dispatched','firmed','running','paused')";

    // Locks in id order, so two commands locking the same lines cannot deadlock.
    private const string LockLines = """
        SELECT e.id, e.site_id, e.code, e.is_schedulable, e.can_run_job,
               coalesce(p.changeover_mode, 'auto') AS changeover_mode
        FROM core.equipment e
        LEFT JOIN core.scheduling_policy p ON p.site_id = e.site_id
        WHERE e.id = ANY(@Ids)
        ORDER BY e.id
        FOR UPDATE OF e
        """;

    private const string JobLine = "SELECT equipment_id FROM core.job WHERE id = @Id";

    private const string JobColumns = """
        id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id,
        planned_start, planned_end, sequence_rank, is_locked, earliest_start, planned_duration,
        counts_as_planned_downtime, status, dispatched_at, firmed_at, replaces_job_id, group_equipment_id, latest_end,
        xmin::text::bigint AS version
        """;

    private const string GetJobForUpdate = $"SELECT {JobColumns} FROM core.job WHERE id = @Id FOR UPDATE";

    private const string WorkOrderLines = $"""
        SELECT DISTINCT equipment_id
        FROM core.job
        WHERE work_order_id = @WorkOrderId AND status IN {OpenStatuses}
        """;

    private const string GetWorkOrderJobsForUpdate = $"""
        SELECT {JobColumns}
        FROM core.job
        WHERE work_order_id = @WorkOrderId AND status IN {OpenStatuses}
        ORDER BY id
        FOR UPDATE
        """;

    private const string Eligible = """
        SELECT EXISTS (
            SELECT 1 FROM core.equipment_capability
            WHERE equipment_id = @LineId
              AND material_id = @MaterialId
              AND effective_from <= @At
              AND (effective_to IS NULL OR effective_to > @At))
        """;

    private const string EligibleInGroup = """
        SELECT EXISTS (
            SELECT 1
            FROM core.equipment_closure cl
            JOIN core.equipment e ON e.id = cl.descendant_id AND e.can_run_job AND e.is_active
            JOIN core.equipment_capability c ON c.equipment_id = e.id
            WHERE cl.ancestor_id = @GroupId
              AND cl.depth > 0
              AND c.material_id = @MaterialId
              AND c.effective_from <= @At
              AND (c.effective_to IS NULL OR c.effective_to > @At))
        """;

    private const string IsUnder = """
        SELECT EXISTS (
            SELECT 1 FROM core.equipment_closure
            WHERE ancestor_id = @GroupId AND descendant_id = @LineId AND depth > 0)
        """;

    private const string GoodQty = "SELECT coalesce(sum(good_qty), 0) FROM core.job_run WHERE job_id = @JobId";

    private const string EndOpenRuns = """
        UPDATE core.job_run
        SET ended_at = @At,
            status = 'ended'
        WHERE job_id = @JobId AND ended_at IS NULL
        """;

    // Ranks either side of the end of the line.
    private const string GapAtEnd = $"""
        SELECT max(sequence_rank) AS before, NULL::numeric AS after
        FROM core.job
        WHERE equipment_id = @LineId AND status IN {OpenStatuses} AND id IS DISTINCT FROM @ExcludingId
        """;

    // Ranks either side of the slot before the target job, in (rank, job_number) order; no row if
    // the target is not an open job on the line.
    private const string GapBefore = $"""
        WITH open_job AS (
            SELECT id, sequence_rank, job_number
            FROM core.job
            WHERE equipment_id = @LineId AND status IN {OpenStatuses} AND id IS DISTINCT FROM @ExcludingId
        )
        SELECT (SELECT o.sequence_rank
                FROM open_job o
                WHERE (o.sequence_rank, o.job_number) < (t.sequence_rank, t.job_number)
                ORDER BY o.sequence_rank DESC, o.job_number DESC
                LIMIT 1) AS before,
               t.sequence_rank AS after
        FROM open_job t
        WHERE t.id = @BeforeJobId
        """;

    private const string Renumber = $"""
        UPDATE core.job j
        SET sequence_rank = r.rank,
            updated_at = @At
        FROM (
            SELECT id, row_number() OVER (ORDER BY sequence_rank, job_number) AS rank
            FROM core.job
            WHERE equipment_id = @LineId AND status IN {OpenStatuses}
        ) r
        WHERE j.id = r.id
        """;

    private const string InsertJob = """
        INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id,
                              planned_qty, uom_id, planned_start, planned_end, sequence_rank, is_locked,
                              earliest_start, planned_duration, counts_as_planned_downtime, status,
                              dispatched_at, firmed_at, replaces_job_id, group_equipment_id, latest_end,
                              created_at, updated_at)
        VALUES (@Id, @SiteId, @WorkOrderId, @JobNumber, @JobType, @EquipmentId, @MaterialId,
                @PlannedQty, @UomId, @PlannedStart, @PlannedEnd, @SequenceRank, @IsLocked,
                @EarliestStart, @PlannedDuration, @CountsAsPlannedDowntime, @Status,
                @DispatchedAt, @FirmedAt, @ReplacesJobId, @GroupEquipmentId, @LatestEnd,
                @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateJob = """
        UPDATE core.job
        SET equipment_id = @EquipmentId,
            sequence_rank = @SequenceRank,
            is_locked = @IsLocked,
            earliest_start = @EarliestStart,
            latest_end = @LatestEnd,
            status = @Status,
            dispatched_at = @DispatchedAt,
            firmed_at = @FirmedAt,
            planned_start = @PlannedStart,
            planned_end = @PlannedEnd,
            updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string InsertStatusChange = """
        INSERT INTO ts.job_status_change (id, occurred_at, job_id, from_status, to_status)
        VALUES (@Id, @At, @JobId, @FromStatus, @ToStatus)
        """;

    private const string GetRunStandardForUpdate = """
        SELECT id, equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at,
               modified_rate_per_hour, modified_at, modified_by, modified_note, xmin::text::bigint AS version
        FROM core.run_standard
        WHERE id = @Id
        FOR UPDATE
        """;

    private const string UpdateRunStandardModification = """
        UPDATE core.run_standard
        SET modified_rate_per_hour = @ModifiedRatePerHour,
            modified_at = @ModifiedAt,
            modified_by = @ModifiedBy,
            modified_note = @ModifiedNote,
            updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string GetPolicyForUpdate = """
        SELECT changeover_mode, xmin::text::bigint AS version
        FROM core.scheduling_policy
        WHERE site_id = @SiteId
        FOR UPDATE
        """;

    private const string InsertPolicy = """
        INSERT INTO core.scheduling_policy (site_id, changeover_mode, updated_at)
        VALUES (@SiteId, @ChangeoverMode, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdatePolicy = """
        UPDATE core.scheduling_policy
        SET changeover_mode = @ChangeoverMode,
            updated_at = @At
        WHERE site_id = @SiteId
        RETURNING xmin::text::bigint
        """;

    public async Task<IReadOnlyList<SchedulingLine>> LockLinesAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync<LineRow>(LockLines, new { Ids = lineIds.ToArray() }, cancellationToken);
        return [.. rows.Select(r => new SchedulingLine(r.Id, r.SiteId, r.Code, r.IsSchedulable, r.ChangeoverMode, r.CanRunJob))];
    }

    public Task<Guid?> GetJobLineAsync(Guid jobId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(JobLine, new { Id = jobId }, cancellationToken);

    public async Task<Versioned<Job>?> GetJobForUpdateAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<JobRow>(GetJobForUpdate, new { Id = jobId }, cancellationToken);
        return row?.ToVersioned();
    }

    public Task<IReadOnlyList<Guid>> GetWorkOrderLinesAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(WorkOrderLines, new { WorkOrderId = workOrderId }, cancellationToken);

    public async Task<IReadOnlyList<Versioned<Job>>> GetWorkOrderJobsForUpdateAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync<JobRow>(GetWorkOrderJobsForUpdate, new { WorkOrderId = workOrderId }, cancellationToken);
        return [.. rows.Select(r => r.ToVersioned())];
    }

    public Task<bool> IsEligibleAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(Eligible, new { LineId = lineId, MaterialId = materialId, At = at }, cancellationToken);

    public Task<bool> IsEligibleInGroupAsync(Guid groupId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(EligibleInGroup, new { GroupId = groupId, MaterialId = materialId, At = at }, cancellationToken);

    public Task<bool> IsUnderAsync(Guid groupId, Guid lineId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsUnder, new { GroupId = groupId, LineId = lineId }, cancellationToken);

    public Task<decimal> GetGoodQtyAsync(Guid jobId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<decimal>(GoodQty, new { JobId = jobId }, cancellationToken);

    public Task EndOpenRunsAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(EndOpenRuns, new { JobId = jobId, At = at }, cancellationToken);

    public async Task<RankGap?> FindGapAsync(Guid lineId, Guid? beforeJobId, Guid? excludingJobId, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<GapRow>(
            beforeJobId is null ? GapAtEnd : GapBefore,
            new { LineId = lineId, BeforeJobId = beforeJobId, ExcludingId = excludingJobId },
            cancellationToken);
        return row is null ? null : new RankGap(row.Before, row.After);
    }

    public Task RenumberAsync(Guid lineId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(Renumber, new { LineId = lineId, At = at }, cancellationToken);

    public Task<long> InsertJobAsync(Job job, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertJob, WithTime(job, at), cancellationToken);

    public Task<long> UpdateJobAsync(Job job, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateJob, WithTime(job, at), cancellationToken);

    public Task AppendStatusChangeAsync(
        Guid jobId, string? fromStatus, string toStatus, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            InsertStatusChange,
            new { Id = ids.NewId(), At = at, JobId = jobId, FromStatus = fromStatus, ToStatus = toStatus },
            cancellationToken);

    public Task<LinePlanSource?> GetLinePlanAsync(
        Guid lineId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        session.UseConnectionAsync((connection, transaction) =>
            LinePlanLoader.LoadAsync(connection, transaction, lineId, windowStart, windowEnd, cancellationToken));

    public async Task<Versioned<RunStandard>?> GetRunStandardForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<RunStandardRow>(GetRunStandardForUpdate, new { Id = id }, cancellationToken);
        return row?.ToVersioned();
    }

    public Task<long> UpdateRunStandardModificationAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters(standard);
        parameters.Add("At", at);
        return session.ExecuteScalarAsync<long>(UpdateRunStandardModification, parameters, cancellationToken);
    }

    public async Task<Versioned<string>?> GetPolicyForUpdateAsync(Guid siteId, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<PolicyRow>(GetPolicyForUpdate, new { SiteId = siteId }, cancellationToken);
        return row is null ? null : new Versioned<string>(row.ChangeoverMode, row.Version);
    }

    public Task<long> InsertPolicyAsync(Guid siteId, string changeoverMode, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            InsertPolicy, new { SiteId = siteId, ChangeoverMode = changeoverMode, At = at }, cancellationToken);

    public Task<long> UpdatePolicyAsync(Guid siteId, string changeoverMode, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            UpdatePolicy, new { SiteId = siteId, ChangeoverMode = changeoverMode, At = at }, cancellationToken);

    private static DynamicParameters WithTime(Job job, DateTimeOffset at)
    {
        var parameters = new DynamicParameters(job);
        parameters.Add("At", at);
        return parameters;
    }

    private sealed class LineRow
    {
        public Guid Id { get; init; }
        public Guid? SiteId { get; init; }
        public string Code { get; init; } = "";
        public bool IsSchedulable { get; init; }
        public bool CanRunJob { get; init; }
        public string ChangeoverMode { get; init; } = "";
    }

    private sealed class GapRow
    {
        public decimal? Before { get; init; }
        public decimal? After { get; init; }
    }

    private sealed class PolicyRow
    {
        public string ChangeoverMode { get; init; } = "";
        public long Version { get; init; }
    }

    /// <summary>Column shape for Dapper; mapped to the domain record so SQL never shapes the domain.</summary>
    private sealed class JobRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid? WorkOrderId { get; init; }
        public string JobNumber { get; init; } = "";
        public string JobType { get; init; } = "";
        public Guid EquipmentId { get; init; }
        public Guid? MaterialId { get; init; }
        public decimal? PlannedQty { get; init; }
        public Guid? UomId { get; init; }
        public DateTimeOffset? PlannedStart { get; init; }
        public DateTimeOffset? PlannedEnd { get; init; }
        public decimal SequenceRank { get; init; }
        public bool IsLocked { get; init; }
        public DateTimeOffset? EarliestStart { get; init; }
        public TimeSpan? PlannedDuration { get; init; }
        public bool CountsAsPlannedDowntime { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? DispatchedAt { get; init; }
        public DateTimeOffset? FirmedAt { get; init; }
        public Guid? ReplacesJobId { get; init; }
        public Guid? GroupEquipmentId { get; init; }
        public DateTimeOffset? LatestEnd { get; init; }
        public long Version { get; init; }

        public Versioned<Job> ToVersioned() => new(
            new Job(
                Id, SiteId, WorkOrderId, JobNumber, JobType, EquipmentId, MaterialId, PlannedQty, UomId,
                PlannedStart, PlannedEnd, SequenceRank, IsLocked, EarliestStart, PlannedDuration,
                CountsAsPlannedDowntime, Status, DispatchedAt, FirmedAt, ReplacesJobId, GroupEquipmentId, LatestEnd),
            Version);
    }

    private sealed class RunStandardRow
    {
        public Guid Id { get; init; }
        public Guid EquipmentId { get; init; }
        public Guid MaterialId { get; init; }
        public Guid UomId { get; init; }
        public decimal CalculatedRatePerHour { get; init; }
        public string CalculationBasis { get; init; } = "";
        public DateTimeOffset CalculatedAt { get; init; }
        public decimal? ModifiedRatePerHour { get; init; }
        public DateTimeOffset? ModifiedAt { get; init; }
        public string? ModifiedBy { get; init; }
        public string? ModifiedNote { get; init; }
        public long Version { get; init; }

        public Versioned<RunStandard> ToVersioned() => new(
            new RunStandard(
                Id, EquipmentId, MaterialId, UomId, CalculatedRatePerHour, CalculationBasis, CalculatedAt,
                ModifiedRatePerHour, ModifiedAt, ModifiedBy, ModifiedNote),
            Version);
    }
}
