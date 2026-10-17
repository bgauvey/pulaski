namespace Pulaski.Infrastructure.Execution;

/// <summary>SQL shared by the execution repository and read store (D-035).</summary>
internal static class ExecutionSql
{
    public const string RunColumns = """
        id, job_id, equipment_id, run_no, started_at, ended_at, status, end_reason_code_id, good_qty, scrap_qty, uom_id
        """;

    // The process links into the job's operation (D-023), labelled "operation 10 Extrude".
    public const string UpstreamLinks = """
        SELECT d.from_operation_id, 'operation ' || o.operation_number || ' ' || o.name AS from_operation_label,
               d.dependency_type, d.lag
        FROM core.job j
        JOIN core.process_dependency d ON d.to_operation_id = j.process_operation_id
        JOIN core.process_operation o ON o.id = d.from_operation_id
        WHERE j.id = @JobId
        ORDER BY o.operation_number
        """;

    // The order's jobs on those upstream operations, with when each first started.
    public const string UpstreamJobs = """
        SELECT u.process_operation_id, u.job_number, u.status,
               (SELECT min(r.started_at) FROM core.job_run r WHERE r.job_id = u.id) AS first_started_at,
               u.completed_at
        FROM core.job j
        JOIN core.job u ON u.work_order_id = j.work_order_id AND u.id <> j.id
        WHERE j.id = @JobId
          AND u.process_operation_id IN (SELECT d.from_operation_id FROM core.process_dependency d WHERE d.to_operation_id = j.process_operation_id)
        ORDER BY u.job_number
        """;

    public const string IdealRate = """
        SELECT ideal_rate_per_hour AS rate_per_hour, rate_uom_id AS uom_id
        FROM core.equipment_capability
        WHERE equipment_id = @LineId AND material_id = @MaterialId
          AND effective_from <= @At AND (effective_to IS NULL OR effective_to > @At)
        LIMIT 1
        """;

    public const string Policy = """
        SELECT rate_tolerance_pct, consumption_tolerance_pct FROM core.execution_policy WHERE site_id = @SiteId
        """;
}
