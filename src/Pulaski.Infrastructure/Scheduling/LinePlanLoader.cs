using Dapper;
using Npgsql;
using Pulaski.Application.Scheduling;
using Pulaski.Application.Units;
using Pulaski.Infrastructure.Units;
using Pulaski.Application.Shifts;
using Pulaski.Domain.Shifts;
using Pulaski.Infrastructure.Shifts;

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
               j.latest_end, j.group_equipment_id, j.process_operation_id
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

    // The nearest equipment, the line first, that doesn't inherit its working time (D-038).
    private const string CalendarSource = """
        SELECT e.code AS source_code, e.calendar_mode AS mode, s.code, s.name, s.cycle_days, s.cycle_start, s.shifts::text AS shifts_json,
               s.overrides::text AS overrides_json, site.time_zone
        FROM core.equipment_closure c
        JOIN core.equipment e ON e.id = c.ancestor_id
        LEFT JOIN core.shift_schedule s ON s.id = e.shift_schedule_id
        LEFT JOIN core.equipment site ON site.id = s.site_id
        WHERE c.descendant_id = @LineId AND e.calendar_mode <> 'inherit'
        ORDER BY c.depth
        LIMIT 1
        """;

    // Exceptions apply from every ancestor, including the line itself.
    private const string Exceptions = """
        SELECT x.starts_at, x.ends_at
        FROM core.calendar_exception x
        JOIN core.equipment_closure c ON c.ancestor_id = x.scope_equipment_id
        WHERE c.descendant_id = @LineId
          AND x.is_active
          AND x.ends_at > @From
          AND x.starts_at < @To
        ORDER BY x.starts_at
        """;

    // The process links into the jobs (D-046): each job of the same work order on the link's earlier operation, with its line,
    // whether it waits on a group, its window and what has happened to it.
    private const string Links = """
        SELECT d.id AS link_id, j.id AS job_id, u.id AS from_job_id, d.dependency_type, d.lag,
               fo.operation_number AS from_operation_number, fo.name AS from_operation_name,
               u.status AS from_status, u.equipment_id AS from_equipment_id, (ue.is_schedulable AND NOT ue.can_run_job) AS from_is_on_group,
               u.earliest_start AS from_earliest_start, u.latest_end AS from_latest_end,
               r.started_at AS from_started_at, r.ended_at AS from_ended_at
        FROM core.job j
        JOIN core.process_dependency d ON d.to_operation_id = j.process_operation_id
        JOIN core.process_operation fo ON fo.id = d.from_operation_id
        JOIN core.job u ON u.work_order_id = j.work_order_id AND u.process_operation_id = d.from_operation_id AND u.status <> 'cancelled'
        LEFT JOIN core.equipment ue ON ue.id = u.equipment_id
        LEFT JOIN LATERAL (
            SELECT min(jr.started_at) AS started_at, max(jr.ended_at) AS ended_at FROM core.job_run jr WHERE jr.job_id = u.id
        ) r ON true
        WHERE j.id = ANY(@JobIds)
        """;

    /// <summary>The most lines a linked projection loads, so a sprawling site can't make one timeline load everything.</summary>
    private const int MaxLinkedLines = 40;

    private static readonly string[] OpenStatuses = ["planned", "dispatched", "firmed", "running", "paused"];

    /// <summary>
    /// The line's plan with the lines it is linked to (D-046): following each open job's process links to the jobs it follows,
    /// and those jobs' lines' links in turn, so the projection can hold every job until the work it follows allows. Null if the
    /// equipment does not exist. Pass the command's transaction, or null for a plain read.
    /// </summary>
    public static async Task<LinePlanSource?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid lineId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var root = await LoadLineAsync(connection, transaction, lineId, windowStart, windowEnd, cancellationToken);
        if (root is null || root.Line.IsGroup)
        {
            return root;
        }

        var lines = new Dictionary<Guid, LinePlanSource> { [lineId] = root };
        var links = new List<JobLinkRow>();
        var pending = new Queue<LinePlanSource>([root]);
        while (pending.TryDequeue(out var source))
        {
            Guid[] jobIds = [.. source.Jobs.Where(j => j.ProcessOperationId is not null && j.WorkOrderId is not null).Select(j => j.JobId)];
            if (jobIds.Length == 0)
            {
                continue;
            }

            var found = (await connection.QueryAsync<JobLinkRow>(new CommandDefinition(Links, new { JobIds = jobIds }, transaction, cancellationToken: cancellationToken))).AsList();
            links.AddRange(found);
            foreach (var upstream in found
                .Where(l => !l.FromIsOnGroup && l.FromEquipmentId is not null && OpenStatuses.Contains(l.FromStatus))
                .Select(l => l.FromEquipmentId!.Value)
                .Distinct())
            {
                if (lines.Count < MaxLinkedLines && !lines.ContainsKey(upstream)
                    && await LoadLineAsync(connection, transaction, upstream, windowStart, windowEnd, cancellationToken) is { Line.IsGroup: false } loaded)
                {
                    lines[upstream] = loaded;
                    pending.Enqueue(loaded);
                }
            }
        }

        return root with { Links = links, Upstream = [.. lines.Values.Where(l => l.Line.Id != lineId)] };
    }

    /// <summary>One line's plan inputs, without its links.</summary>
    private static async Task<LinePlanSource?> LoadLineAsync(
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

        var calendar = await connection.QuerySingleOrDefaultAsync<CalendarRow>(Command(CalendarSource, args));
        var (hasCalendar, shifts, calendarName) = Shifts(calendar, windowStart, windowEnd);

        var jobs = await ListAsync<PlanJobRow>(Jobs, args);
        var runners = line.IsGroup ? await ListAsync<RunnerRateRow>(Runners, args) : [];

        // Units and the conversions of every material on the line, to size jobs entered in another unit than their rate (D-026).
        Guid[] materialIds = [.. jobs.Select(j => j.MaterialId).OfType<Guid>().Concat(runners.Select(r => r.MaterialId)).Distinct()];
        var units = (await ListAsync<UnitRow>(UnitSql.Units, new { })).Select(u => u.ToDomain()).ToList();
        var conversions = materialIds.Length == 0 ? [] : await ListAsync<MaterialConversionRow>(UnitSql.Conversions, new { MaterialIds = materialIds });

        return new LinePlanSource
        {
            Line = line,
            Jobs = jobs,
            Units = units,
            Conversions = conversions,
            PreviousMaterial = await connection.QuerySingleOrDefaultAsync<PlanMaterialRow>(Command(PreviousMaterial, args)),
            Rates = await ListAsync<RunRateRow>(Rates, args),
            ChangeoverRules = await ListAsync<ChangeoverRuleRow>(ChangeoverRules, args),
            StandardChangeovers = await ListAsync<StandardChangeoverRow>(StandardChangeovers, args),
            HasShiftCalendar = hasCalendar,
            Shifts = shifts,
            CalendarName = calendarName,
            Exceptions = await ListAsync<WindowRow>(Exceptions, args),
            Runners = runners,
        };
    }

    // Working time comes from the schedule, worked out for the window and a month past it, so work running past the
    // horizon still lands in shifts. Chosen around-the-clock time is a calendar too: it isn't warned about.
    private static (bool HasCalendar, IReadOnlyList<WindowRow> Shifts, string? Name) Shifts(CalendarRow? calendar, DateTimeOffset from, DateTimeOffset to)
    {
        switch (calendar)
        {
            case { Mode: CalendarModes.AroundTheClock }:
                return (true, [new WindowRow { StartsAt = DateTimeOffset.MinValue, EndsAt = DateTimeOffset.MaxValue }], $"Around the clock, from {calendar.SourceCode}");
            case { Mode: CalendarModes.Schedule, Code: { } code, TimeZone: { } timeZone }:
                var schedule = ShiftMapping.ToDomain(code, calendar.Name ?? code, calendar.CycleDays, calendar.CycleStart, ShiftSql.Read(calendar.ShiftsJson ?? "[]"),
                    ShiftSql.ReadOverrides(calendar.OverridesJson));
                var windows = schedule.Expand(TimeZoneInfo.FindSystemTimeZoneById(timeZone), from, to.AddDays(31))
                    .Select(o => new WindowRow { StartsAt = o.Start, EndsAt = o.End })
                    .ToList();
                return (true, windows, $"{code} · {calendar.Name}, from {calendar.SourceCode}");
            default:
                return (false, [], null);
        }
    }

    private sealed class CalendarRow
    {
        public string SourceCode { get; init; } = "";
        public string Mode { get; init; } = "";
        public string? Code { get; init; }
        public string? Name { get; init; }
        public int CycleDays { get; init; }
        public DateOnly CycleStart { get; init; }
        public string? ShiftsJson { get; init; }
        public string? OverridesJson { get; init; }
        public string? TimeZone { get; init; }
    }
}
