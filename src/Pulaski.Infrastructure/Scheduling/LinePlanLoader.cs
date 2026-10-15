using Dapper;
using Npgsql;
using Pulaski.Application.Scheduling;

namespace Pulaski.Infrastructure.Scheduling;

/// <summary>
/// Loads one line's plan inputs. Shared by the timeline query (its own connection, no locks) and
/// by dispatch and publish, which read inside the command's transaction after locking the line.
/// </summary>
internal static class LinePlanLoader
{
    private const string Line = """
        SELECT e.id, e.code, e.name, e.is_schedulable, e.can_run_job,
               coalesce(p.changeover_mode, 'auto') AS changeover_mode
        FROM core.equipment e
        LEFT JOIN core.scheduling_policy p ON p.site_id = e.site_id
        WHERE e.id = @LineId
        """;

    // Open jobs in planner sequence; the running job first. Good quantity and first start
    // are summed over the job's runs on any equipment.
    private const string Jobs = """
        SELECT j.id AS job_id, j.job_number, j.job_type, j.status,
               j.material_id, m.code AS material_code, m.material_class_id,
               j.planned_qty, j.uom_id, u.code AS uom_code,
               coalesce(r.good_qty, 0) AS good_qty, r.started_at,
               j.earliest_start, j.planned_duration, j.planned_start, j.planned_end, j.is_locked,
               j.xmin::text::bigint AS version,
               j.work_order_id, w.order_number AS work_order_number, w.due_at,
               j.latest_end, j.group_equipment_id
        FROM core.job j
        LEFT JOIN core.material m   ON m.id = j.material_id
        LEFT JOIN core.uom u        ON u.id = j.uom_id
        LEFT JOIN core.work_order w ON w.id = j.work_order_id
        LEFT JOIN LATERAL (
            SELECT sum(jr.good_qty) AS good_qty, min(jr.started_at) AS started_at
            FROM core.job_run jr
            WHERE jr.job_id = j.id
        ) r ON true
        WHERE j.equipment_id = @LineId
          AND j.status IN ('planned','dispatched','firmed','running','paused')
        ORDER BY (j.status = 'running') DESC, j.sequence_rank, j.job_number
        """;

    private const string PreviousMaterial = """
        SELECT j.material_id, m.code AS material_code, m.material_class_id
        FROM core.job_run jr
        JOIN core.job j      ON j.id = jr.job_id
        JOIN core.material m ON m.id = j.material_id
        WHERE jr.equipment_id = @LineId
          AND jr.ended_at IS NOT NULL
          AND j.job_type = 'production'
        ORDER BY jr.ended_at DESC
        LIMIT 1
        """;

    private const string Rates = """
        SELECT material_id, uom_id, effective_rate_per_hour AS rate_per_hour, rate_source
        FROM core.run_standard
        WHERE equipment_id = @LineId
        """;

    private const string ChangeoverRules = """
        SELECT id, equipment_id, from_material_id, from_material_class_id,
               to_material_id, to_material_class_id, duration
        FROM core.changeover_rule
        WHERE site_id = (SELECT site_id FROM core.equipment WHERE id = @LineId)
          AND is_active
          AND (equipment_id IS NULL OR equipment_id = @LineId)
        """;

    private const string StandardChangeovers = """
        SELECT material_id, standard_changeover
        FROM core.equipment_capability
        WHERE equipment_id = @LineId
          AND standard_changeover IS NOT NULL
          AND effective_from <= @From
          AND (effective_to IS NULL OR effective_to > @From)
        """;

    // A group's runners (D-022): active lines below it that can run jobs, one row per material
    // they have a capability for, with the run standard's rate when there is one.
    private const string Runners = """
        SELECT e.id AS line_id, e.code AS line_code, c.material_id,
               rs.uom_id, rs.effective_rate_per_hour AS rate_per_hour, rs.rate_source
        FROM core.equipment_closure cl
        JOIN core.equipment e ON e.id = cl.descendant_id AND e.can_run_job AND e.is_active
        JOIN core.equipment_capability c ON c.equipment_id = e.id
                                        AND c.effective_from <= @From
                                        AND (c.effective_to IS NULL OR c.effective_to > @From)
        LEFT JOIN core.run_standard rs ON rs.equipment_id = e.id AND rs.material_id = c.material_id
        WHERE cl.ancestor_id = @LineId
          AND cl.depth > 0
        ORDER BY e.code
        """;

    // Shifts come from the nearest ancestor (or the line itself) that has any instances.
    private const string ShiftScope = """
        SELECT c.ancestor_id
        FROM core.equipment_closure c
        WHERE c.descendant_id = @LineId
          AND EXISTS (SELECT 1 FROM core.shift_instance si WHERE si.scope_equipment_id = c.ancestor_id)
        ORDER BY c.depth
        LIMIT 1
        """;

    private const string Shifts = """
        SELECT starts_at, ends_at
        FROM core.shift_instance
        WHERE scope_equipment_id = @ScopeId
          AND is_scheduled
          AND ends_at > @From
          AND starts_at < @To
        ORDER BY starts_at
        """;

    // Exceptions apply from every ancestor, including the line itself.
    private const string Exceptions = """
        SELECT x.starts_at, x.ends_at
        FROM core.calendar_exception x
        JOIN core.equipment_closure c ON c.ancestor_id = x.scope_equipment_id
        WHERE c.descendant_id = @LineId
          AND x.ends_at > @From
          AND x.starts_at < @To
        ORDER BY x.starts_at
        """;

    /// <summary>Null if the equipment does not exist. Pass the command's transaction, or null for a plain read.</summary>
    public static async Task<LinePlanSource?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid lineId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var args = new { LineId = lineId, From = windowStart, To = windowEnd };

        CommandDefinition Command(string sql, object parameters) =>
            new(sql, parameters, transaction, cancellationToken: cancellationToken);

        async Task<IReadOnlyList<T>> ListAsync<T>(string sql, object parameters) =>
            (await connection.QueryAsync<T>(Command(sql, parameters))).AsList();

        var line = await connection.QuerySingleOrDefaultAsync<SchedulingLineRow>(Command(Line, args));
        if (line is null)
        {
            return null;
        }

        var scopeId = await connection.QuerySingleOrDefaultAsync<Guid?>(Command(ShiftScope, args));
        var shifts = scopeId is { } scope
            ? await ListAsync<WindowRow>(Shifts, new { ScopeId = scope, From = windowStart, To = windowEnd })
            : [];

        return new LinePlanSource
        {
            Line = line,
            Jobs = await ListAsync<PlanJobRow>(Jobs, args),
            PreviousMaterial = await connection.QuerySingleOrDefaultAsync<PlanMaterialRow>(Command(PreviousMaterial, args)),
            Rates = await ListAsync<RunRateRow>(Rates, args),
            ChangeoverRules = await ListAsync<ChangeoverRuleRow>(ChangeoverRules, args),
            StandardChangeovers = await ListAsync<StandardChangeoverRow>(StandardChangeovers, args),
            HasShiftCalendar = scopeId is not null,
            Shifts = shifts,
            Exceptions = await ListAsync<WindowRow>(Exceptions, args),
            Runners = line.IsGroup ? await ListAsync<RunnerRateRow>(Runners, args) : [],
        };
    }
}
