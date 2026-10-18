using Dapper;
using Npgsql;
using Pulaski.Application.Execution;
using Pulaski.Application.Units;
using Pulaski.Contracts.Execution;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Units;

namespace Pulaski.Infrastructure.Execution;

/// <summary>Read side for execution (D-035): the floor board and a job's runs and reports.</summary>
internal sealed class ExecutionQueries(NpgsqlDataSource dataSource, IUnitReadStore units) : IExecutionReadStore
{
    private const int NextPerLine = 6;

    // Lines that run jobs, and departments with jobs waiting on them (D-022).
    private const string BoardLines = """
        SELECT e.id AS line_id, e.code AS line_code, e.name AS line_name, NOT e.can_run_job AS is_group
        FROM core.equipment e
        WHERE e.site_id = @SiteId AND e.is_active
          AND (e.can_run_job
               OR (e.is_schedulable AND EXISTS (SELECT 1 FROM core.job j WHERE j.equipment_id = e.id
                                                AND j.status IN ('planned','dispatched','firmed','running','paused'))))
        ORDER BY NOT e.can_run_job, e.code
        """;

    private const string JobColumns = """
        j.id AS job_id, j.job_number, j.job_type, j.status, j.xmin::text::bigint AS version, j.site_id, j.equipment_id,
        e.code AS line_code, j.group_equipment_id, j.work_order_id, wo.order_number AS work_order_number, wo.status AS work_order_status,
        j.material_id, m.code AS material_code, m.name AS material_name, j.planned_qty, j.uom_id, u.code AS uom_code,
        j.process_operation_id, j.completed_at, site.time_zone AS site_time_zone,
        m.batch_size, m.batch_tolerance_pct, m.batch_rule, m.base_uom_id, bu.code AS base_uom_code,
        coalesce((SELECT sum(r.good_qty) FROM core.job_run r WHERE r.job_id = j.id), 0) AS good_qty,
        coalesce((SELECT sum(r.scrap_qty) FROM core.job_run r WHERE r.job_id = j.id), 0) AS scrap_qty
        """;

    private const string JobFrom = """
        FROM core.job j
        JOIN core.equipment e ON e.id = j.equipment_id
        LEFT JOIN core.equipment site ON site.id = j.site_id
        LEFT JOIN core.work_order wo ON wo.id = j.work_order_id
        LEFT JOIN core.material m ON m.id = j.material_id
        LEFT JOIN core.uom u ON u.id = j.uom_id
        LEFT JOIN core.uom bu ON bu.id = m.base_uom_id
        """;

    private const string BoardJobs = $"""
        SELECT {JobColumns}
        {JobFrom}
        WHERE j.site_id = @SiteId AND j.status IN ('planned','dispatched','firmed','running','paused')
        ORDER BY j.equipment_id, j.sequence_rank, j.job_number
        """;

    private const string JobById = $"""
        SELECT {JobColumns}
        {JobFrom}
        WHERE j.id = @JobId
        """;

    private const string OpenRuns = $"""
        SELECT {ExecutionSql.RunColumns}
        FROM core.job_run
        WHERE ended_at IS NULL AND equipment_id IN (SELECT id FROM core.equipment WHERE site_id = @SiteId)
        """;

    private const string Runs = """
        SELECT r.id, r.run_no, e.code AS line_code, r.started_at, r.ended_at, rc.name AS end_reason, r.good_qty, r.scrap_qty, r.equipment_id,
               r.end_reason_code_id IS NOT NULL AS ended_with_reason,
               EXISTS (SELECT 1 FROM core.partial_batch p WHERE p.run_id = r.id) AS has_partial_batch
        FROM core.job_run r
        JOIN core.equipment e ON e.id = r.equipment_id
        LEFT JOIN core.reason_code rc ON rc.id = r.end_reason_code_id
        WHERE r.job_id = @JobId
        ORDER BY r.run_no
        """;

    // One report writes a processed row and, with scrap, a defective row in the same transaction, so they share recorded_at.
    // Machine counts (D-041) aren't reports.
    private const string Reports = """
        SELECT c.run_id, r.run_no, max(c.occurred_at) AS occurred_at, c.recorded_at,
               coalesce(sum(c.delta_qty) FILTER (WHERE c.count_kind = 'processed'), 0)
                 - coalesce(sum(c.delta_qty) FILTER (WHERE c.count_kind = 'defective'), 0) AS good_qty,
               coalesce(sum(c.delta_qty) FILTER (WHERE c.count_kind = 'defective'), 0) AS scrap_qty,
               max(rc.name) AS scrap_reason, max(c.actor_id) AS actor_id, max(c.comment) AS comment,
               bool_or(r.ended_at IS NOT NULL AND c.occurred_at = r.ended_at AND c.recorded_at > r.ended_at) AS is_late
        FROM ts.production_count c
        JOIN core.job_run r ON r.id = c.run_id
        LEFT JOIN core.reason_code rc ON rc.id = c.scrap_reason_code_id
        WHERE c.job_id = @JobId AND c.source <> 'machine'
        GROUP BY c.run_id, r.run_no, c.recorded_at
        ORDER BY c.recorded_at DESC
        """;

    private const string PartialSelect = """
        SELECT p.id, p.job_id, j.job_number, e.code AS line_code, m.code AS material_code, p.run_id, r.run_no, p.lot_id, l.lot_number,
               p.quantity, u.code AS uom_code, p.batch_size, p.batch_rule, p.status, p.occurred_at, p.reported_by,
               p.decided_at, p.decided_by, p.decision_comment
        FROM core.partial_batch p
        JOIN core.job j ON j.id = p.job_id
        JOIN core.job_run r ON r.id = p.run_id
        JOIN core.equipment e ON e.id = r.equipment_id
        JOIN core.lot l ON l.id = p.lot_id
        JOIN core.material m ON m.id = l.material_id
        JOIN core.uom u ON u.id = p.uom_id
        """;

    private const string JobPartials = $"""
        {PartialSelect}
        WHERE p.job_id = @JobId
        ORDER BY p.occurred_at
        """;

    private const string PendingPartials = $"""
        {PartialSelect}
        WHERE p.status = 'pending' AND j.site_id = @SiteId
        ORDER BY p.occurred_at
        """;

    private const string IsSite = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @SiteId AND level = 'site')";

    public async Task<IReadOnlyList<LineBoardRowDto>> GetLineBoardAsync(Guid siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { SiteId = siteId };
        var lines = (await connection.QueryAsync<LineRow>(new CommandDefinition(BoardLines, args, cancellationToken: cancellationToken))).AsList();
        var jobs = (await connection.QueryAsync<JobRow>(new CommandDefinition(BoardJobs, args, cancellationToken: cancellationToken))).AsList();
        var runs = (await connection.QueryAsync<ExecutionRepository.RunRow>(new CommandDefinition(OpenRuns, args, cancellationToken: cancellationToken)))
            .Select(r => r.ToDomain()).ToDictionary(r => r.JobId);
        var policy = await PolicyAsync(connection, siteId, cancellationToken);
        var allUnits = await units.GetUnitsAsync(cancellationToken);

        var rows = new List<LineBoardRowDto>();
        foreach (var line in lines)
        {
            var onLine = jobs.Where(j => j.EquipmentId == line.LineId).ToList();
            var runningJob = onLine.FirstOrDefault(j => runs.ContainsKey(j.JobId));
            RunningJobDto? running = null;
            if (runningJob is not null)
            {
                var run = runs[runningJob.JobId];
                running = new RunningJobDto
                {
                    JobId = runningJob.JobId,
                    JobNumber = runningJob.JobNumber,
                    JobType = runningJob.JobType,
                    Version = runningJob.Version,
                    MaterialCode = runningJob.MaterialCode,
                    WorkOrderNumber = runningJob.WorkOrderNumber,
                    PlannedQty = runningJob.PlannedQty,
                    UomCode = runningJob.UomCode,
                    RunId = run.Id,
                    RunNo = run.RunNo,
                    StartedAt = run.StartedAt,
                    GoodQty = runningJob.GoodQty,
                    ScrapQty = runningJob.ScrapQty,
                    RunGoodQty = run.GoodQty,
                    RunScrapQty = run.ScrapQty,
                    CapQty = await IdealRateAsync(connection, allUnits, runningJob, run, cancellationToken) is { } rate
                        ? RateCap.Cap(rate, policy.RateTolerancePct, run.Elapsed(now))
                        : null,
                    BatchSize = runningJob.BatchSize,
                    BatchRule = runningJob.BatchRule,
                    BaseUomCode = runningJob.BaseUomCode,
                };
            }

            var next = new List<BoardJobDto>();
            foreach (var job in onLine.Where(j => j != runningJob).Take(NextPerLine))
            {
                var readiness = await ReadinessAsync(connection, job, now, cancellationToken);
                next.Add(new BoardJobDto
                {
                    JobId = job.JobId,
                    JobNumber = job.JobNumber,
                    JobType = job.JobType,
                    Status = job.Status,
                    Version = job.Version,
                    MaterialCode = job.MaterialCode,
                    WorkOrderNumber = job.WorkOrderNumber,
                    PlannedQty = job.PlannedQty,
                    UomCode = job.UomCode,
                    GoodQty = job.GoodQty,
                    IsReady = readiness.IsReady,
                    Waiting = readiness.Waiting,
                    ReadyAt = readiness.ReadyAt,
                });
            }

            rows.Add(new LineBoardRowDto
            {
                LineId = line.LineId,
                LineCode = line.LineCode,
                LineName = line.LineName,
                IsGroup = line.IsGroup,
                Running = running,
                Next = next,
            });
        }

        return rows;
    }

    public async Task<JobExecutionDto?> GetJobAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { JobId = jobId };
        var job = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(JobById, args, cancellationToken: cancellationToken));
        if (job is null)
        {
            return null;
        }

        var policy = await PolicyAsync(connection, job.SiteId, cancellationToken);
        var allUnits = await units.GetUnitsAsync(cancellationToken);
        var runRows = (await connection.QueryAsync<RunViewRow>(new CommandDefinition(Runs, args, cancellationToken: cancellationToken))).AsList();
        var reports = (await connection.QueryAsync<ProductionReportDto>(new CommandDefinition(Reports, args, cancellationToken: cancellationToken))).AsList();
        var partials = (await connection.QueryAsync<PartialBatchDto>(new CommandDefinition(JobPartials, args, cancellationToken: cancellationToken))).AsList();
        var readiness = JobStatusIsStarted(job.Status) ? Readiness.Ready : await ReadinessAsync(connection, job, now, cancellationToken);

        decimal? idealRate = null;
        if (runRows.Count > 0)
        {
            var first = runRows[0];
            idealRate = await IdealRateAsync(
                connection, allUnits, job,
                new JobRun(first.Id, job.JobId, first.EquipmentId, first.RunNo, first.StartedAt, first.EndedAt, "", null, 0, 0, job.UomId), cancellationToken);
        }

        return new JobExecutionDto
        {
            JobId = job.JobId,
            JobNumber = job.JobNumber,
            JobType = job.JobType,
            Status = job.Status,
            Version = job.Version,
            SiteId = job.SiteId,
            SiteTimeZone = job.SiteTimeZone,
            LineId = job.EquipmentId,
            LineCode = job.LineCode,
            IsOnGroup = job.GroupEquipmentId == job.EquipmentId,
            WorkOrderId = job.WorkOrderId,
            WorkOrderNumber = job.WorkOrderNumber,
            WorkOrderStatus = job.WorkOrderStatus,
            MaterialId = job.MaterialId,
            MaterialCode = job.MaterialCode,
            MaterialName = job.MaterialName,
            PlannedQty = job.PlannedQty,
            UomId = job.UomId,
            UomCode = job.UomCode,
            GoodQty = job.GoodQty,
            ScrapQty = job.ScrapQty,
            CompletedAt = job.CompletedAt,
            IdealRatePerHour = idealRate,
            RateTolerancePct = policy.RateTolerancePct,
            IsReady = readiness.IsReady,
            Waiting = readiness.Waiting,
            ReadyAt = readiness.ReadyAt,
            BatchSize = job.BatchSize,
            BatchTolerancePct = job.BatchTolerancePct,
            BatchRule = job.BatchRule,
            BaseUomId = job.BaseUomId,
            BaseUomCode = job.BaseUomCode,
            PartialBatches = partials,
            Runs = [.. runRows.Select(r => new JobRunDto
            {
                Id = r.Id,
                RunNo = r.RunNo,
                LineCode = r.LineCode,
                StartedAt = r.StartedAt,
                EndedAt = r.EndedAt,
                EndReason = r.EndReason,
                GoodQty = r.GoodQty,
                ScrapQty = r.ScrapQty,
                CapQty = idealRate is { } rate ? RateCap.Cap(rate, policy.RateTolerancePct, (r.EndedAt ?? now) - r.StartedAt) : null,
                EndedWithReason = r.EndedWithReason,
                HasPartialBatch = r.HasPartialBatch,
            })],
            Reports = reports,
        };
    }

    public async Task<ExecutionPolicyDto?> GetPolicyAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(IsSite, new { SiteId = siteId }, cancellationToken: cancellationToken)))
        {
            return null;
        }

        var row = await connection.QuerySingleOrDefaultAsync<ExecutionRepository.PolicyRow>(
            new CommandDefinition(ExecutionSql.Policy, new { SiteId = siteId }, cancellationToken: cancellationToken));
        var policy = row is null ? ExecutionPolicy.Default : new ExecutionPolicy(row.RateTolerancePct, row.ConsumptionTolerancePct);
        return new ExecutionPolicyDto
        {
            SiteId = siteId,
            RateTolerancePct = policy.RateTolerancePct,
            ConsumptionTolerancePct = policy.ConsumptionTolerancePct,
            IsDefault = row is null,
        };
    }

    public async Task<IReadOnlyList<PartialBatchDto>> ListPendingPartialBatchesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<PartialBatchDto>(new CommandDefinition(PendingPartials, new { SiteId = siteId }, cancellationToken: cancellationToken))).AsList();
    }

    private static bool JobStatusIsStarted(string status) => status is "running" or "paused" or "completed" or "closed";

    private static async Task<ExecutionPolicy> PolicyAsync(NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<ExecutionRepository.PolicyRow>(
            new CommandDefinition(ExecutionSql.Policy, new { SiteId = siteId }, cancellationToken: cancellationToken)) is { } r
            ? new ExecutionPolicy(r.RateTolerancePct, r.ConsumptionTolerancePct)
            : ExecutionPolicy.Default;

    /// <summary>Whether a job can start: its order must be on the floor, and its process links met (D-035).</summary>
    private static async Task<Readiness> ReadinessAsync(NpgsqlConnection connection, JobRow job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (job.Status == "paused" || job.JobType != "production")
        {
            return Readiness.Ready;
        }

        if (job.WorkOrderStatus is "planned" || job.Status == "planned")
        {
            return new Readiness(false, $"Work order {job.WorkOrderNumber} is planned; dispatch it first.", null);
        }

        if (job.GroupEquipmentId == job.EquipmentId)
        {
            return new Readiness(true, null, null);
        }

        if (job.ProcessOperationId is null)
        {
            return Readiness.Ready;
        }

        var args = new { job.JobId };
        var links = (await connection.QueryAsync<ExecutionRepository.LinkRow>(new CommandDefinition(ExecutionSql.UpstreamLinks, args, cancellationToken: cancellationToken)))
            .Select(l => l.ToDomain()).ToList();
        if (links.Count == 0)
        {
            return Readiness.Ready;
        }

        var upstream = (await connection.QueryAsync<ExecutionRepository.UpstreamRow>(new CommandDefinition(ExecutionSql.UpstreamJobs, args, cancellationToken: cancellationToken)))
            .Select(u => u.ToDomain()).ToList();
        return JobReadiness.For(links, upstream, now);
    }

    /// <summary>The ideal rate in the job's unit from the capability in effect when the run started; null when there is none or it doesn't convert.</summary>
    private async Task<decimal?> IdealRateAsync(
        NpgsqlConnection connection, IReadOnlyList<Unit> allUnits, JobRow job, JobRun run, CancellationToken cancellationToken)
    {
        if (job.MaterialId is not { } materialId || job.UomId is not { } jobUom)
        {
            return null;
        }

        var rate = await connection.QuerySingleOrDefaultAsync<ExecutionRepository.RateRow>(new CommandDefinition(
            ExecutionSql.IdealRate, new { LineId = run.EquipmentId, MaterialId = materialId, At = run.StartedAt }, cancellationToken: cancellationToken));
        if (rate is null)
        {
            return null;
        }

        var converter = UnitConversions.For(materialId, allUnits, await units.GetConversionsAsync([materialId], cancellationToken));
        return converter.CanConvert(rate.UomId, jobUom) ? converter.Convert(rate.RatePerHour, rate.UomId, jobUom) : null;
    }

    private sealed class LineRow
    {
        public Guid LineId { get; init; }
        public string LineCode { get; init; } = "";
        public string LineName { get; init; } = "";
        public bool IsGroup { get; init; }
    }

    private sealed class JobRow
    {
        public Guid JobId { get; init; }
        public string JobNumber { get; init; } = "";
        public string JobType { get; init; } = "";
        public string Status { get; init; } = "";
        public long Version { get; init; }
        public Guid SiteId { get; init; }
        public Guid EquipmentId { get; init; }
        public string LineCode { get; init; } = "";
        public Guid? GroupEquipmentId { get; init; }
        public Guid? WorkOrderId { get; init; }
        public string? WorkOrderNumber { get; init; }
        public string? WorkOrderStatus { get; init; }
        public Guid? MaterialId { get; init; }
        public string? MaterialCode { get; init; }
        public string? MaterialName { get; init; }
        public decimal? PlannedQty { get; init; }
        public Guid? UomId { get; init; }
        public string? UomCode { get; init; }
        public Guid? ProcessOperationId { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? SiteTimeZone { get; init; }
        public decimal GoodQty { get; init; }
        public decimal ScrapQty { get; init; }
        public decimal? BatchSize { get; init; }
        public decimal BatchTolerancePct { get; init; }
        public string? BatchRule { get; init; }
        public Guid? BaseUomId { get; init; }
        public string? BaseUomCode { get; init; }
    }

    private sealed class RunViewRow
    {
        public Guid Id { get; init; }
        public int RunNo { get; init; }
        public string LineCode { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public string? EndReason { get; init; }
        public decimal GoodQty { get; init; }
        public decimal ScrapQty { get; init; }
        public Guid EquipmentId { get; init; }
        public bool EndedWithReason { get; init; }
        public bool HasPartialBatch { get; init; }
    }
}
