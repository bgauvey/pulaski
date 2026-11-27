using Pulaski.Application.EquipmentModel;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.EquipmentModel;

/// <summary>
/// Copying a line (#140, D-132), in the command's transaction: rows are read from the original and written with new ids from the app; references
/// inside the copied line (its constraint, counter, links, output location) point at the copies, those outside it stay as they were.
/// </summary>
internal sealed class LineCopyRepository(DbSession session) : ILineCopyRepository
{
    // The pieces: the line, then everything below it by depth.
    private const string PiecesSql = """
        SELECT e.id, e.parent_id, c.depth::int AS depth, e.code, e.name, e.level, e.equipment_role AS role
        FROM core.equipment_closure c JOIN core.equipment e ON e.id = c.descendant_id
        WHERE c.ancestor_id = @Id AND e.is_active
        ORDER BY c.depth, e.code
        """;

    private const string CountsSql = """
        WITH s AS (SELECT descendant_id AS id FROM core.equipment_closure WHERE ancestor_id = @Id)
        SELECT
            (SELECT count(*) FROM core.equipment_capability x WHERE x.equipment_id IN (SELECT id FROM s) AND (x.effective_to IS NULL OR x.effective_to > now()))::int AS capabilities,
            (SELECT count(*) FROM core.changeover_rule x WHERE x.equipment_id IN (SELECT id FROM s) AND x.is_active)::int AS changeover_rules,
            (SELECT count(*) FROM core.equipment_stop_reason_map x WHERE x.equipment_id IN (SELECT id FROM s) AND x.is_active)::int AS stop_code_overrides,
            (SELECT count(*) FROM core.equipment_qualification x WHERE x.equipment_id IN (SELECT id FROM s) AND x.is_required)::int AS qualifications,
            (SELECT count(*) FROM core.oee_target x WHERE x.equipment_id IN (SELECT id FROM s) AND x.effective_to IS NULL)::int AS oee_targets,
            (SELECT count(*) FROM core.maintenance_plan x WHERE x.equipment_id IN (SELECT id FROM s) AND x.is_active)::int AS maintenance_plans,
            (SELECT count(*) FROM core.work_instruction_target x WHERE x.equipment_id IN (SELECT id FROM s) AND x.is_active)::int AS instruction_targets,
            (SELECT count(*) FROM core.equipment_tag x WHERE x.equipment_id IN (SELECT id FROM s) AND x.is_active)::int AS tags
        """;

    private const string Map = "map AS (SELECT * FROM unnest(@Src::uuid[], @Dst::uuid[]) AS m(src, dst))";

    public async Task<LineCopySourceLine?> LoadAsync(Guid lineId, CancellationToken cancellationToken)
    {
        var line = await session.QuerySingleOrDefaultAsync<LineRow>(
            "SELECT id, code, site_id, parent_id, can_run_job FROM core.equipment WHERE id = @Id AND is_active AND level <> 'site' AND level <> 'enterprise'",
            new { Id = lineId }, cancellationToken);
        if (line is null)
        {
            return null;
        }

        var pieces = (await session.QueryAsync<LineCopySource>(PiecesSql, new { Id = lineId }, cancellationToken)).ToList();
        var c = await session.QuerySingleOrDefaultAsync<CountsRow>(CountsSql, new { Id = lineId }, cancellationToken) ?? new CountsRow();
        return new LineCopySourceLine(line.Id, line.Code, line.SiteId, line.ParentId, line.CanRunJob, pieces, c.Capabilities, c.ChangeoverRules,
            c.StopCodeOverrides, c.Qualifications, c.OeeTargets, c.MaintenancePlans, c.InstructionTargets, c.Tags);
    }

    public async Task<IReadOnlySet<string>> CodesAsync(Guid siteId, CancellationToken cancellationToken) =>
        new HashSet<string>(await session.QueryAsync<string>("SELECT code FROM core.equipment WHERE site_id = @SiteId", new { SiteId = siteId }, cancellationToken),
            StringComparer.OrdinalIgnoreCase);

    public async Task<bool> LockParentAsync(Guid parentId, Guid siteId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM core.equipment WHERE id = @Id AND site_id = @SiteId AND level IN ('site', 'area') AND is_active FOR SHARE",
            new { Id = parentId, SiteId = siteId }, cancellationToken) is not null;

    public async Task CopyEquipmentAsync(IReadOnlyList<LineCopyRow> rows, bool machineSettings, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            await session.ExecuteAsync(
                """
                INSERT INTO core.equipment (id, parent_id, site_id, code, name, description, level, equipment_role, tracks_state, is_schedulable, can_run_job,
                    holds_inventory, commingles_lots, flow_model, min_residence, empty_level, level_tolerance, single_material, state_derivation,
                    micro_stop_threshold, calendar_mode, shift_schedule_id, machine_profile_id, label_printer_id, output_location_id,
                    constraint_equipment_id, production_counter_equipment_id, is_active, created_at, updated_at)
                SELECT @NewId, @ParentId, site_id, @Code, @Name, description, level, equipment_role, tracks_state, is_schedulable, can_run_job,
                    holds_inventory, commingles_lots, flow_model, min_residence, empty_level, level_tolerance, single_material,
                    -- A constraint machine is named once its copy exists (below); until then the line derives its state from its own tags.
                    CASE WHEN state_derivation = 'constraint_machine' THEN 'own_tags' ELSE state_derivation END,
                    micro_stop_threshold, calendar_mode, shift_schedule_id, machine_profile_id, label_printer_id, output_location_id,
                    NULL, NULL, true, @At, @At
                FROM core.equipment WHERE id = @SourceId;
                INSERT INTO core.equipment_closure (ancestor_id, descendant_id, depth)
                SELECT c.ancestor_id, @NewId, c.depth + 1 FROM core.equipment_closure c WHERE c.descendant_id = @ParentId
                UNION ALL SELECT @NewId, @NewId, 0;
                """, new { row.NewId, ParentId = row.NewParentId, row.Code, row.Name, row.SourceId, At = at }, cancellationToken);
        }

        var args = Args(rows);
        // References: inside the copy, to the copies; outside it (an output location at the site), as they were; without machine settings, none.
        await session.ExecuteAsync(
            $"""
            WITH {Map}
            UPDATE core.equipment n
            SET constraint_equipment_id = CASE WHEN @Settings THEN (SELECT dst FROM map WHERE src = o.constraint_equipment_id) END,
                state_derivation = CASE WHEN @Settings AND (SELECT dst FROM map WHERE src = o.constraint_equipment_id) IS NOT NULL THEN o.state_derivation
                                        WHEN o.state_derivation = 'constraint_machine' THEN 'own_tags' ELSE o.state_derivation END,
                production_counter_equipment_id = CASE WHEN @Settings THEN (SELECT dst FROM map WHERE src = o.production_counter_equipment_id) END,
                output_location_id = CASE WHEN @Settings THEN coalesce((SELECT dst FROM map WHERE src = o.output_location_id), o.output_location_id) END,
                machine_profile_id = CASE WHEN @Settings THEN o.machine_profile_id END,
                label_printer_id = CASE WHEN @Settings THEN o.label_printer_id END
            FROM map JOIN core.equipment o ON o.id = map.src
            WHERE n.id = map.dst
            """, new { args.Src, args.Dst, Settings = machineSettings }, cancellationToken);

        // Material flow between the copied pieces.
        var links = await session.QueryAsync<(Guid From, Guid To, string Kind)>(
            $"""
            WITH {Map}
            SELECT f.dst, t.dst, l.link_kind FROM core.equipment_link l JOIN map f ON f.src = l.from_equipment_id JOIN map t ON t.src = l.to_equipment_id
            WHERE l.is_active
            """, new { args.Src, args.Dst }, cancellationToken);
        foreach (var (from, to, kind) in links)
        {
            await session.ExecuteAsync(
                "INSERT INTO core.equipment_link (id, from_equipment_id, to_equipment_id, link_kind, is_active) VALUES (@Id, @From, @To, @Kind, true)",
                new { Id = newId(), From = from, To = to, Kind = kind }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Guid>> CopyCapabilitiesAsync(IReadOnlyList<LineCopyRow> rows, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var args = Args(rows);
        var capabilities = (await session.QueryAsync<(Guid Id, Guid To)>(
            $"""
            WITH {Map}
            SELECT x.id, map.dst FROM core.equipment_capability x JOIN map ON map.src = x.equipment_id
            WHERE x.effective_to IS NULL OR x.effective_to > @At
            ORDER BY x.effective_from
            """, new { args.Src, args.Dst, At = at }, cancellationToken)).ToList();
        var created = new List<Guid>();
        foreach (var (sourceId, to) in capabilities)
        {
            var id = newId();
            created.Add(id);
            // A capability from now; its planning rate from the ideal rate, as one added by hand (D-034).
            await session.ExecuteAsync(
                """
                INSERT INTO core.equipment_capability (id, equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, standard_changeover, standard_crew_size,
                    is_preferred, batch_size, batch_tolerance_pct, units_per_count, lot_size, lot_hours, lot_by_shift, effective_from, effective_to)
                SELECT @Id, @To, material_id, ideal_rate_per_hour, rate_uom_id, standard_changeover, standard_crew_size,
                    is_preferred, batch_size, batch_tolerance_pct, units_per_count, lot_size, lot_hours, lot_by_shift, @At, NULL
                FROM core.equipment_capability WHERE id = @SourceId;
                INSERT INTO core.run_standard (id, equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at, created_at, updated_at)
                SELECT @RunStandardId, @To, material_id, rate_uom_id, ideal_rate_per_hour, 'ideal_rate', @At, @At, @At
                FROM core.equipment_capability WHERE id = @SourceId
                ON CONFLICT (equipment_id, material_id) DO NOTHING;
                """, new { Id = id, To = to, SourceId = sourceId, RunStandardId = newId(), At = at }, cancellationToken);
        }

        var rules = (await session.QueryAsync<(Guid Id, Guid To)>(
            $"WITH {Map} SELECT x.id, map.dst FROM core.changeover_rule x JOIN map ON map.src = x.equipment_id WHERE x.is_active",
            new { args.Src, args.Dst }, cancellationToken)).ToList();
        foreach (var (sourceId, to) in rules)
        {
            await session.ExecuteAsync(
                """
                INSERT INTO core.changeover_rule (id, site_id, equipment_id, from_material_id, from_material_class_id, to_material_id, to_material_class_id,
                    duration, note, is_active, created_at, updated_at)
                SELECT @Id, site_id, @To, from_material_id, from_material_class_id, to_material_id, to_material_class_id, duration, note, true, @At, @At
                FROM core.changeover_rule WHERE id = @SourceId
                """, new { Id = newId(), To = to, SourceId = sourceId, At = at }, cancellationToken);
        }

        return created;
    }

    public Task CopyStopCodesAsync(IReadOnlyList<LineCopyRow> rows, CancellationToken cancellationToken)
    {
        var args = Args(rows);
        return session.ExecuteAsync(
            $"""
            WITH {Map}
            INSERT INTO core.equipment_stop_reason_map (equipment_id, plc_reason_value, reason_code_id, is_active)
            SELECT map.dst, x.plc_reason_value, x.reason_code_id, true FROM core.equipment_stop_reason_map x JOIN map ON map.src = x.equipment_id WHERE x.is_active
            """, new { args.Src, args.Dst }, cancellationToken);
    }

    public async Task CopyRequirementsAsync(IReadOnlyList<LineCopyRow> rows, Func<Guid> newId, DateTimeOffset at, string by, CancellationToken cancellationToken)
    {
        var args = Args(rows);
        await session.ExecuteAsync(
            $"""
            WITH {Map}
            INSERT INTO core.equipment_qualification (equipment_id, qualification_id, is_required, updated_at)
            SELECT map.dst, x.qualification_id, true, @At FROM core.equipment_qualification x JOIN map ON map.src = x.equipment_id WHERE x.is_required
            """, new { args.Src, args.Dst, At = at }, cancellationToken);

        var targets = (await session.QueryAsync<(Guid Id, Guid To)>(
            $"WITH {Map} SELECT x.id, map.dst FROM core.oee_target x JOIN map ON map.src = x.equipment_id WHERE x.effective_to IS NULL",
            new { args.Src, args.Dst }, cancellationToken)).ToList();
        foreach (var (sourceId, to) in targets)
        {
            await session.ExecuteAsync(
                """
                INSERT INTO core.oee_target (id, site_id, equipment_id, effective_from, effective_to, availability_pct, performance_pct, quality_pct, oee_pct, created_at, updated_at)
                SELECT @Id, site_id, @To, greatest(effective_from, @Today), NULL, availability_pct, performance_pct, quality_pct, oee_pct, @At, @At
                FROM core.oee_target WHERE id = @SourceId
                """, new { Id = newId(), To = to, SourceId = sourceId, At = at, Today = DateOnly.FromDateTime(at.UtcDateTime).ToDateTime(TimeOnly.MinValue) }, cancellationToken);
        }

        var plans = (await session.QueryAsync<(Guid Id, Guid To)>(
            $"WITH {Map} SELECT x.id, map.dst FROM core.maintenance_plan x JOIN map ON map.src = x.equipment_id WHERE x.is_active",
            new { args.Src, args.Dst }, cancellationToken)).ToList();
        foreach (var (sourceId, to) in plans)
        {
            await session.ExecuteAsync(
                """
                INSERT INTO core.maintenance_plan (id, site_id, equipment_id, name, meter, interval_value, priority, is_active, started_at, created_at, created_by, updated_at)
                SELECT @Id, site_id, @To, name, meter, interval_value, priority, true, @At, @At, @By, @At FROM core.maintenance_plan WHERE id = @SourceId
                """, new { Id = newId(), To = to, SourceId = sourceId, At = at, By = by }, cancellationToken);
        }

        var instructions = (await session.QueryAsync<(Guid Id, Guid To)>(
            $"WITH {Map} SELECT x.id, map.dst FROM core.work_instruction_target x JOIN map ON map.src = x.equipment_id WHERE x.is_active",
            new { args.Src, args.Dst }, cancellationToken)).ToList();
        foreach (var (sourceId, to) in instructions)
        {
            await session.ExecuteAsync(
                """
                INSERT INTO core.work_instruction_target (id, instruction_id, kind, process_id, operation_number, material_id, equipment_id, is_active, created_at, updated_at)
                SELECT @Id, instruction_id, kind, process_id, operation_number, material_id, @To, true, @At, @At FROM core.work_instruction_target WHERE id = @SourceId
                """, new { Id = newId(), To = to, SourceId = sourceId, At = at }, cancellationToken);
        }
    }

    private static (Guid[] Src, Guid[] Dst) Args(IReadOnlyList<LineCopyRow> rows) => ([.. rows.Select(r => r.SourceId)], [.. rows.Select(r => r.NewId)]);

    private sealed class LineRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public Guid SiteId { get; init; }
        public Guid? ParentId { get; init; }
        public bool CanRunJob { get; init; }
    }

    private sealed class CountsRow
    {
        public int Capabilities { get; init; }
        public int ChangeoverRules { get; init; }
        public int StopCodeOverrides { get; init; }
        public int Qualifications { get; init; }
        public int OeeTargets { get; init; }
        public int MaintenancePlans { get; init; }
        public int InstructionTargets { get; init; }
        public int Tags { get; init; }
    }
}
