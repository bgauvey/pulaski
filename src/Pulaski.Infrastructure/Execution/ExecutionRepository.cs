using Pulaski.Application.Abstractions;
using Pulaski.Application.Execution;
using Dapper;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Materials;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Execution;

/// <summary>Write side for execution (D-035). Every statement runs in the command's transaction.</summary>
internal sealed class ExecutionRepository(DbSession session, IIdGenerator ids) : IExecutionRepository
{
    private const string JobWorkOrder = "SELECT work_order_id FROM core.job WHERE id = @Id";

    private const string JobNumberExists = "SELECT EXISTS (SELECT 1 FROM core.job WHERE site_id = @SiteId AND job_number = @Number)";

    private const string OpenRun = $"SELECT {ExecutionSql.RunColumns} FROM core.job_run WHERE job_id = @JobId AND ended_at IS NULL FOR UPDATE";

    private const string RunById = $"SELECT {ExecutionSql.RunColumns} FROM core.job_run WHERE id = @Id FOR UPDATE";

    private const string RunAt = $"""
        SELECT {ExecutionSql.RunColumns} FROM core.job_run
        WHERE job_id = @JobId AND started_at <= @At AND (ended_at IS NULL OR ended_at >= @At)
        ORDER BY started_at DESC
        LIMIT 1
        FOR UPDATE
        """;

    private const string RunningOnLine = """
        SELECT j.job_number FROM core.job_run r JOIN core.job j ON j.id = r.job_id
        WHERE r.equipment_id = @LineId AND r.ended_at IS NULL AND r.job_id <> @JobId
        LIMIT 1
        """;

    private const string LastRunJob = """
        SELECT j.job_type, j.material_id, m.code AS material_code
        FROM core.job_run r
        JOIN core.job j ON j.id = r.job_id
        LEFT JOIN core.material m ON m.id = j.material_id
        WHERE r.equipment_id = @LineId
        ORDER BY r.started_at DESC
        LIMIT 1
        """;

    private const string NextRunNo = "SELECT coalesce(max(run_no), 0) + 1 FROM core.job_run WHERE job_id = @JobId";

    private const string InsertRun = """
        INSERT INTO core.job_run (id, job_id, equipment_id, run_no, started_at, ended_at, status, end_reason_code_id, good_qty, scrap_qty, uom_id)
        VALUES (@Id, @JobId, @EquipmentId, @RunNo, @StartedAt, @EndedAt, @Status, @EndReasonCodeId, @GoodQty, @ScrapQty, @UomId)
        """;

    private const string UpdateRun = """
        UPDATE core.job_run
        SET ended_at = @EndedAt, status = @Status, end_reason_code_id = @EndReasonCodeId, good_qty = @GoodQty, scrap_qty = @ScrapQty
        WHERE id = @Id
        """;

    private const string InsertCount = """
        INSERT INTO ts.production_count (id, occurred_at, equipment_id, run_id, job_id, count_kind, delta_qty, uom_id,
                                         scrap_reason_code_id, source, actor_id, comment, recorded_at)
        VALUES (@Id, @OccurredAt, @EquipmentId, @RunId, @JobId, @CountKind, @DeltaQty, @UomId,
                @ScrapReasonCodeId, 'operator', @ActorId, @Comment, @RecordedAt)
        """;

    private const string UpsertPolicy = """
        INSERT INTO core.execution_policy (site_id, rate_tolerance_pct, consumption_tolerance_pct, updated_at)
        VALUES (@SiteId, @RateTolerancePct, @ConsumptionTolerancePct, @At)
        ON CONFLICT (site_id) DO UPDATE
        SET rate_tolerance_pct = EXCLUDED.rate_tolerance_pct, consumption_tolerance_pct = EXCLUDED.consumption_tolerance_pct, updated_at = EXCLUDED.updated_at
        RETURNING xmin::text::bigint
        """;

    private const string PlannedComponent = """
        SELECT sum(planned_qty) AS quantity, uom_id
        FROM core.job_material
        WHERE job_id = @JobId AND material_id = @MaterialId AND direction = 'consume'
        GROUP BY uom_id
        ORDER BY 1 DESC
        LIMIT 1
        """;

    private const string Consumed = """
        SELECT coalesce(-sum(e.qty_delta), 0)
        FROM ts.lot_event e
        JOIN core.lot l ON l.id = e.lot_id
        WHERE e.job_id = @JobId AND e.event_kind = 'consumed' AND l.material_id = @MaterialId
        """;

    private const string Made = "SELECT coalesce(sum(good_qty + scrap_qty), 0) FROM core.job_run WHERE job_id = @JobId";

    private const string Downstream = """
        SELECT d2.id AS job_id, d2.job_number, e.code AS line_code, d2.status
        FROM core.job j
        JOIN core.process_dependency d ON d.from_operation_id = j.process_operation_id
        JOIN core.job d2 ON d2.work_order_id = j.work_order_id AND d2.process_operation_id = d.to_operation_id
        JOIN core.equipment e ON e.id = d2.equipment_id
        WHERE j.id = @JobId
        ORDER BY d2.job_number
        """;

    private const string CloseCompleted = """
        UPDATE core.job SET status = 'closed', updated_at = @At
        WHERE work_order_id = @WorkOrderId AND status = 'completed'
        RETURNING id
        """;

    private const string InsertStatusChange = """
        INSERT INTO ts.job_status_change (id, occurred_at, job_id, from_status, to_status)
        VALUES (@Id, @At, @JobId, 'completed', 'closed')
        """;

    private const string PartialColumns = """
        id, job_id, run_id, lot_id, quantity, uom_id, batch_size, batch_rule, status, occurred_at, reported_by,
        decided_at, decided_by, decision_comment, scrap_reason_code_id
        """;

    private const string RunHasPartial = "SELECT EXISTS (SELECT 1 FROM core.partial_batch WHERE run_id = @RunId)";

    private const string InsertPartial = """
        INSERT INTO core.partial_batch (id, job_id, run_id, lot_id, quantity, uom_id, batch_size, batch_rule, status, occurred_at, reported_by,
                                        decided_at, decided_by, decision_comment, scrap_reason_code_id, created_at, updated_at)
        VALUES (@Id, @JobId, @RunId, @LotId, @Quantity, @UomId, @BatchSize, @BatchRule, @Status, @OccurredAt, @ReportedBy,
                @DecidedAt, @DecidedBy, @DecisionComment, @ScrapReasonCodeId, @At, @At)
        """;

    private const string PartialForUpdate = $"SELECT {PartialColumns} FROM core.partial_batch WHERE id = @Id FOR UPDATE";

    private const string UpdatePartial = """
        UPDATE core.partial_batch
        SET status = @Status, decided_at = @DecidedAt, decided_by = @DecidedBy, decision_comment = @DecisionComment,
            scrap_reason_code_id = @ScrapReasonCodeId, updated_at = @At
        WHERE id = @Id
        """;

    private const string FindReason = """
        SELECT id FROM core.reason_code
        WHERE reason_kind = @Kind AND code = @Code AND is_active AND is_selectable AND (site_id = @SiteId OR site_id IS NULL)
        ORDER BY site_id NULLS LAST
        LIMIT 1
        """;

    public Task<bool> RunHasPartialBatchAsync(Guid runId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(RunHasPartial, new { RunId = runId }, cancellationToken);

    public Task InsertPartialBatchAsync(PartialBatch batch, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(InsertPartial, PartialArgs(batch, at), cancellationToken);

    public async Task<PartialBatch?> GetPartialBatchForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<PartialRow>(PartialForUpdate, new { Id = id }, cancellationToken))?.ToDomain();

    public Task UpdatePartialBatchAsync(PartialBatch batch, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpdatePartial, PartialArgs(batch, at), cancellationToken);

    public Task<Guid?> FindReasonAsync(string kind, string code, Guid siteId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(FindReason, new { Kind = kind, Code = code, SiteId = siteId }, cancellationToken);

    private static DynamicParameters PartialArgs(PartialBatch batch, DateTimeOffset at)
    {
        var args = new DynamicParameters(batch);
        args.Add("At", at);
        return args;
    }

    public Task<Guid?> GetJobWorkOrderAsync(Guid jobId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(JobWorkOrder, new { Id = jobId }, cancellationToken);

    public Task<bool> JobNumberExistsAsync(Guid siteId, string jobNumber, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(JobNumberExists, new { SiteId = siteId, Number = jobNumber }, cancellationToken);

    public async Task<JobRun?> GetOpenRunForUpdateAsync(Guid jobId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<RunRow>(OpenRun, new { JobId = jobId }, cancellationToken))?.ToDomain();

    public async Task<JobRun?> GetRunForUpdateAsync(Guid runId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<RunRow>(RunById, new { Id = runId }, cancellationToken))?.ToDomain();

    public async Task<JobRun?> GetRunAtForUpdateAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<RunRow>(RunAt, new { JobId = jobId, At = at }, cancellationToken))?.ToDomain();

    public Task<string?> GetRunningJobOnLineAsync(Guid lineId, Guid exceptJobId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<string?>(RunningOnLine, new { LineId = lineId, JobId = exceptJobId }, cancellationToken);

    public async Task<(string JobType, Guid? MaterialId, string? MaterialCode)?> GetLastRunJobAsync(Guid lineId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LastRunRow>(LastRunJob, new { LineId = lineId }, cancellationToken) is { } r
            ? (r.JobType, r.MaterialId, r.MaterialCode)
            : null;

    public Task<int> NextRunNoAsync(Guid jobId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<int>(NextRunNo, new { JobId = jobId }, cancellationToken);

    public Task InsertRunAsync(JobRun run, CancellationToken cancellationToken) => session.ExecuteAsync(InsertRun, run, cancellationToken);

    public Task UpdateRunAsync(JobRun run, CancellationToken cancellationToken) => session.ExecuteAsync(UpdateRun, run, cancellationToken);

    public Task InsertCountAsync(ProductionCountRow count, CancellationToken cancellationToken) =>
        session.ExecuteAsync(InsertCount, count, cancellationToken);

    public async Task<ExecutionPolicy> GetPolicyAsync(Guid siteId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<PolicyRow>(ExecutionSql.Policy, new { SiteId = siteId }, cancellationToken) is { } r
            ? new ExecutionPolicy(r.RateTolerancePct, r.ConsumptionTolerancePct)
            : ExecutionPolicy.Default;

    public async Task<Versioned<ExecutionPolicy>?> GetPolicyForUpdateAsync(Guid siteId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<PolicyRow>(ExecutionSql.Policy + "\nFOR UPDATE", new { SiteId = siteId }, cancellationToken) is { } r
            ? new Versioned<ExecutionPolicy>(new ExecutionPolicy(r.RateTolerancePct, r.ConsumptionTolerancePct), r.Version)
            : null;

    public Task<long> UpsertPolicyAsync(Guid siteId, ExecutionPolicy policy, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            UpsertPolicy, new { SiteId = siteId, policy.RateTolerancePct, policy.ConsumptionTolerancePct, At = at }, cancellationToken);

    public async Task<(decimal RatePerHour, Guid UomId)?> GetIdealRateAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<RateRow>(ExecutionSql.IdealRate, new { LineId = lineId, MaterialId = materialId, At = at }, cancellationToken) is { } r
            ? (r.RatePerHour, r.UomId)
            : null;

    public async Task<(decimal Quantity, Guid UomId)?> GetPlannedComponentAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<QuantityRow>(PlannedComponent, new { JobId = jobId, MaterialId = materialId }, cancellationToken) is { } r
            ? (r.Quantity, r.UomId)
            : null;

    public Task<decimal> GetConsumedAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<decimal>(Consumed, new { JobId = jobId, MaterialId = materialId }, cancellationToken);

    public Task<decimal> GetMadeAsync(Guid jobId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<decimal>(Made, new { JobId = jobId }, cancellationToken);

    public async Task<(IReadOnlyList<UpstreamLink> Links, IReadOnlyList<UpstreamJob> Jobs)> GetUpstreamAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var links = await session.QueryAsync<LinkRow>(ExecutionSql.UpstreamLinks, new { JobId = jobId }, cancellationToken);
        if (links.Count == 0)
        {
            return ([], []);
        }

        var jobs = await session.QueryAsync<UpstreamRow>(ExecutionSql.UpstreamJobs, new { JobId = jobId }, cancellationToken);
        return ([.. links.Select(l => l.ToDomain())], [.. jobs.Select(j => j.ToDomain())]);
    }

    public async Task<IReadOnlyList<DownstreamJob>> GetDownstreamJobsAsync(Guid jobId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<DownstreamRow>(Downstream, new { JobId = jobId }, cancellationToken))
            .Select(r => new DownstreamJob(r.JobId, r.JobNumber, r.LineCode, r.Status))];

    public async Task<int> CloseCompletedJobsAsync(Guid workOrderId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var closed = await session.QueryAsync<Guid>(CloseCompleted, new { WorkOrderId = workOrderId, At = at }, cancellationToken);
        foreach (var jobId in closed)
        {
            await session.ExecuteAsync(InsertStatusChange, new { Id = ids.NewId(), At = at, JobId = jobId }, cancellationToken);
        }

        return closed.Count;
    }

    internal sealed class RunRow
    {
        public Guid Id { get; init; }
        public Guid JobId { get; init; }
        public Guid EquipmentId { get; init; }
        public int RunNo { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public string Status { get; init; } = "";
        public Guid? EndReasonCodeId { get; init; }
        public decimal GoodQty { get; init; }
        public decimal ScrapQty { get; init; }
        public Guid? UomId { get; init; }

        public JobRun ToDomain() => new(Id, JobId, EquipmentId, RunNo, StartedAt, EndedAt, Status, EndReasonCodeId, GoodQty, ScrapQty, UomId);
    }

    private sealed class PartialRow
    {
        public Guid Id { get; init; }
        public Guid JobId { get; init; }
        public Guid RunId { get; init; }
        public Guid LotId { get; init; }
        public decimal Quantity { get; init; }
        public Guid UomId { get; init; }
        public decimal BatchSize { get; init; }
        public string BatchRule { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTimeOffset OccurredAt { get; init; }
        public string? ReportedBy { get; init; }
        public DateTimeOffset? DecidedAt { get; init; }
        public string? DecidedBy { get; init; }
        public string? DecisionComment { get; init; }
        public Guid? ScrapReasonCodeId { get; init; }

        public PartialBatch ToDomain() => new(
            Id, JobId, RunId, LotId, Quantity, UomId, BatchSize, BatchRule, Status, OccurredAt, ReportedBy, DecidedAt, DecidedBy, DecisionComment, ScrapReasonCodeId);
    }

    internal sealed class LinkRow
    {
        public Guid FromOperationId { get; init; }
        public string FromOperationLabel { get; init; } = "";
        public string DependencyType { get; init; } = "";
        public TimeSpan Lag { get; init; }

        public UpstreamLink ToDomain() => new(FromOperationId, FromOperationLabel, DependencyType, Lag);
    }

    internal sealed class UpstreamRow
    {
        public Guid ProcessOperationId { get; init; }
        public string JobNumber { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTimeOffset? FirstStartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }

        public UpstreamJob ToDomain() => new(ProcessOperationId, JobNumber, Status, FirstStartedAt, CompletedAt);
    }

    internal sealed class PolicyRow
    {
        public decimal RateTolerancePct { get; init; }
        public decimal ConsumptionTolerancePct { get; init; }
        public long Version { get; init; }
    }

    private sealed class LastRunRow
    {
        public string JobType { get; init; } = "";
        public Guid? MaterialId { get; init; }
        public string? MaterialCode { get; init; }
    }

    internal sealed class RateRow
    {
        public decimal RatePerHour { get; init; }
        public Guid UomId { get; init; }
    }

    private sealed class DownstreamRow
    {
        public Guid JobId { get; init; }
        public string JobNumber { get; init; } = "";
        public string LineCode { get; init; } = "";
        public string Status { get; init; } = "";
    }

    private sealed class QuantityRow
    {
        public decimal Quantity { get; init; }
        public Guid UomId { get; init; }
    }
}
