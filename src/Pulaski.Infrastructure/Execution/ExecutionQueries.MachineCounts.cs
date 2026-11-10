using Dapper;
using Pulaski.Contracts.Execution;
using Pulaski.Domain.Execution;

namespace Pulaski.Infrastructure.Execution;

/// <summary>What a run's machine counter counted (#27, D-097), to prefill a report from the machine and reconcile the run.</summary>
internal sealed partial class ExecutionQueries
{
    private const string MachineJob = """
        SELECT j.id, j.material_id, m.base_uom_id, bu.code AS base_uom_code, ju.code AS job_uom_code
        FROM core.job j
        LEFT JOIN core.material m ON m.id = j.material_id
        LEFT JOIN core.uom bu ON bu.id = m.base_uom_id
        LEFT JOIN core.uom ju ON ju.id = j.uom_id
        WHERE j.id = @JobId
        """;

    // The run named, else the open one, else the latest.
    private const string MachineRun = """
        SELECT id, run_no, equipment_id, started_at, ended_at, good_qty, scrap_qty
        FROM core.job_run
        WHERE job_id = @JobId AND (@RunId::uuid IS NULL OR id = @RunId)
        ORDER BY (ended_at IS NULL) DESC, started_at DESC
        LIMIT 1
        """;

    // Where the line's output is counted: its setting, else its own processed counter, else the only one below it.
    private const string MachineCounter = """
        WITH counters AS (
            SELECT DISTINCT t.equipment_id
            FROM core.equipment_tag t
            JOIN core.equipment_closure c ON c.descendant_id = t.equipment_id AND c.ancestor_id = @LineId
            WHERE t.is_active AND t.tag_role = 'processed_count'
        ), chosen AS (
            SELECT CASE
                       WHEN l.production_counter_equipment_id IS NOT NULL THEN l.production_counter_equipment_id
                       WHEN EXISTS (SELECT 1 FROM counters WHERE equipment_id = l.id) THEN l.id
                       WHEN (SELECT count(*) FROM counters) = 1 THEN (SELECT equipment_id FROM counters)
                   END AS id,
                   (SELECT count(*) FROM counters)::int AS counters
            FROM core.equipment l
            WHERE l.id = @LineId
        )
        SELECT ch.id AS counter_id, e.code AS counter_code, ch.counters
        FROM chosen ch
        LEFT JOIN core.equipment e ON e.id = ch.id
        """;

    // The line's capability for the material when the run started: whether there is one, and its units per count.
    private const string MachineCapability = """
        SELECT units_per_count
        FROM core.equipment_capability
        WHERE equipment_id = @LineId AND material_id = @MaterialId AND effective_from <= @At AND (effective_to IS NULL OR effective_to > @At)
        LIMIT 1
        """;

    private const string MachineSums = """
        SELECT coalesce(sum(delta_qty) FILTER (WHERE count_kind = 'processed' AND (@Since::timestamptz IS NULL OR occurred_at > @Since)), 0) AS processed,
               coalesce(sum(delta_qty) FILTER (WHERE count_kind = 'defective' AND (@Since::timestamptz IS NULL OR occurred_at > @Since)), 0) AS defective,
               max(occurred_at) FILTER (WHERE count_kind IN ('processed', 'defective') AND (@Since::timestamptz IS NULL OR occurred_at > @Since)) AS through,
               coalesce(sum(delta_qty) FILTER (WHERE count_kind = 'processed'), 0) AS run_processed,
               coalesce(sum(delta_qty) FILTER (WHERE count_kind = 'defective'), 0) AS run_defective
        FROM ts.production_count
        WHERE equipment_id = @CounterId AND source = 'machine' AND occurred_at >= @From AND occurred_at <= @To
        """;

    public async Task<MachineCountsDto?> GetMachineCountsAsync(Guid jobId, Guid? runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var job = await connection.QuerySingleOrDefaultAsync<MachineJobRow>(new CommandDefinition(MachineJob, new { JobId = jobId }, cancellationToken: cancellationToken));
        if (job is null)
        {
            return null;
        }

        var empty = new MachineCountsDto { UomId = job.BaseUomId, UomCode = job.BaseUomCode, ReportedUomCode = job.JobUomCode };
        var run = await connection.QuerySingleOrDefaultAsync<MachineRunRow>(
            new CommandDefinition(MachineRun, new { JobId = jobId, RunId = runId }, cancellationToken: cancellationToken));
        if (run is null)
        {
            return empty with { Unavailable = runId is null ? "The job hasn't run yet." : "That run isn't one of the job's." };
        }

        var counter = await connection.QuerySingleAsync<MachineCounterRow>(
            new CommandDefinition(MachineCounter, new { LineId = run.EquipmentId }, cancellationToken: cancellationToken));
        var result = empty with
        {
            RunId = run.Id,
            RunNo = run.RunNo,
            CounterEquipmentId = counter.CounterId,
            CounterCode = counter.CounterCode,
            RunReportedGoodQty = run.GoodQty,
            RunReportedScrapQty = run.ScrapQty,
        };
        if (counter.CounterId is not { } counterId)
        {
            return result with
            {
                Unavailable = counter.Counters == 0
                    ? "Nothing on the line has a processed counter."
                    : "Several machines on the line count; choose where production is counted on the line's machine data.",
            };
        }

        if (job.MaterialId is not { } materialId)
        {
            return result with { Unavailable = "The job makes no material." };
        }

        var capability = await connection.QuerySingleOrDefaultAsync<MachineCapabilityRow>(
            new CommandDefinition(MachineCapability, new { LineId = run.EquipmentId, MaterialId = materialId, At = run.StartedAt }, cancellationToken: cancellationToken));
        if (capability is null)
        {
            return result with { Unavailable = "The line had no capability for the material when the run started, so its counts can't be turned into output." };
        }

        var since = (await connection.QuerySingleAsync<MachineCoverageRow>(
            new CommandDefinition(ExecutionSql.MachineCoverage, new { RunId = run.Id }, cancellationToken: cancellationToken))).Through;
        var sums = await connection.QuerySingleAsync<MachineSumsRow>(new CommandDefinition(
            MachineSums, new { CounterId = counterId, Since = since, From = run.StartedAt, To = run.EndedAt ?? now }, cancellationToken: cancellationToken));
        var (good, scrap) = MachineCounts.Output(sums.Processed, sums.Defective, capability.UnitsPerCount);
        var (runGood, runScrap) = MachineCounts.Output(sums.RunProcessed, sums.RunDefective, capability.UnitsPerCount);
        return result with
        {
            Since = since,
            Through = sums.Through,
            ProcessedCount = sums.Processed,
            DefectiveCount = sums.Defective,
            UnitsPerCount = capability.UnitsPerCount ?? 1m,
            GoodQty = good,
            ScrapQty = scrap,
            RunMachineGoodQty = runGood,
            RunMachineScrapQty = runScrap,
        };
    }

    private sealed class MachineJobRow
    {
        public Guid Id { get; init; }
        public Guid? MaterialId { get; init; }
        public Guid? BaseUomId { get; init; }
        public string? BaseUomCode { get; init; }
        public string? JobUomCode { get; init; }
    }

    private sealed class MachineRunRow
    {
        public Guid Id { get; init; }
        public int RunNo { get; init; }
        public Guid EquipmentId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public decimal GoodQty { get; init; }
        public decimal ScrapQty { get; init; }
    }

    private sealed class MachineCounterRow
    {
        public Guid? CounterId { get; init; }
        public string? CounterCode { get; init; }
        public int Counters { get; init; }
    }

    private sealed class MachineCapabilityRow
    {
        public decimal? UnitsPerCount { get; init; }
    }

    private sealed class MachineCoverageRow
    {
        public DateTimeOffset? Through { get; init; }
    }

    private sealed class MachineSumsRow
    {
        public decimal Processed { get; init; }
        public decimal Defective { get; init; }
        public DateTimeOffset? Through { get; init; }
        public decimal RunProcessed { get; init; }
        public decimal RunDefective { get; init; }
    }
}
