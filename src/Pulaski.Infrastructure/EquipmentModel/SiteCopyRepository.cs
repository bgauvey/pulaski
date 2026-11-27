using Pulaski.Application.EquipmentModel;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.EquipmentModel;

/// <summary>
/// Copying a site's reference data to a new site (#140b, D-132), in the command's transaction, with ids from the app. Only what is switched on is
/// copied; dated one-off changes (a shift schedule's overrides) aren't, and calendar exceptions only those still to come.
/// </summary>
internal sealed class SiteCopyRepository(DbSession session) : ISiteCopyRepository
{
    public async Task<SiteCopyCounts> CountAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<SiteCopyCounts>(
            """
            SELECT
                (SELECT count(*) FROM core.shift_schedule WHERE site_id = @Site AND is_active)::int AS shift_schedules,
                (SELECT count(*) FROM core.calendar_exception WHERE scope_equipment_id = @Site AND is_active AND ends_at > @At)::int AS calendar_exceptions,
                (SELECT count(*) FROM core.reason_code WHERE site_id = @Site AND is_active)::int AS reasons,
                (SELECT count(*) FROM core.machine_profile WHERE site_id = @Site AND is_active)::int AS machine_profiles,
                (SELECT count(*) FROM core.crew WHERE site_id = @Site AND is_active)::int AS crews,
                (SELECT count(*) FROM core.qualification WHERE site_id = @Site AND is_active)::int AS qualifications,
                (SELECT count(*) FROM core.andon_type WHERE site_id = @Site AND is_active)::int AS andon_types,
                (SELECT count(*) FROM core.lot_attribute WHERE site_id = @Site AND is_active)::int AS lot_attributes,
                ((SELECT count(*) FROM core.scheduling_policy WHERE site_id = @Site) + (SELECT count(*) FROM core.execution_policy WHERE site_id = @Site)
                 + (SELECT count(*) FROM core.rate_refresh_settings WHERE site_id = @Site) + (SELECT count(*) FROM core.allocation_settings WHERE site_id = @Site)
                 + (SELECT count(*) FROM core.release_approval_settings WHERE site_id = @Site)
                 + (SELECT count(*) FROM core.oee_target WHERE site_id = @Site AND equipment_id IS NULL AND effective_to IS NULL))::int AS settings
            """, new { Site = siteId, At = at }, cancellationToken) ?? new SiteCopyCounts(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public async Task<int> CopyShiftsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var id in await IdsAsync("SELECT id FROM core.shift_schedule WHERE site_id = @Site AND is_active ORDER BY code", fromSite, cancellationToken))
        {
            count += await session.ExecuteAsync(
                """
                INSERT INTO core.shift_schedule (id, site_id, code, name, description, cycle_days, cycle_start, shifts, overrides, is_active, created_at, updated_at)
                SELECT @Id, @To, code, name, description, cycle_days, cycle_start, shifts, '[]'::jsonb, true, @At, @At FROM core.shift_schedule WHERE id = @Source
                """, new { Id = newId(), To = toSite, Source = id, At = at }, cancellationToken);
        }

        foreach (var id in await IdsAsync("SELECT id FROM core.calendar_exception WHERE scope_equipment_id = @Site AND is_active AND ends_at > now() ORDER BY starts_at", fromSite, cancellationToken))
        {
            count += await session.ExecuteAsync(
                """
                INSERT INTO core.calendar_exception (id, scope_equipment_id, kind, starts_at, ends_at, note, is_active, created_at, updated_at)
                SELECT @Id, @To, kind, starts_at, ends_at, note, true, @At, @At FROM core.calendar_exception WHERE id = @Source
                """, new { Id = newId(), To = toSite, Source = id, At = at }, cancellationToken);
        }

        return count;
    }

    public async Task<int> CopyReasonsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // Groups before their members: a reason is copied once its parent is (or its parent isn't the site's own).
        var reasons = (await session.QueryAsync<(Guid Id, Guid? ParentId)>(
            "SELECT id, parent_id FROM core.reason_code WHERE site_id = @Site AND is_active", new { Site = fromSite }, cancellationToken)).ToList();
        var own = reasons.Select(r => r.Id).ToHashSet();
        var map = new Dictionary<Guid, Guid>();
        while (map.Count < reasons.Count)
        {
            var ready = reasons.Where(r => !map.ContainsKey(r.Id) && (r.ParentId is not { } p || !own.Contains(p) || map.ContainsKey(p))).ToList();
            if (ready.Count == 0)
            {
                break;
            }

            foreach (var r in ready)
            {
                var id = newId();
                map[r.Id] = id;
                await session.ExecuteAsync(
                    """
                    INSERT INTO core.reason_code (id, site_id, parent_id, reason_kind, utilization_class, code, name, oee_impact, is_selectable, requires_comment, sort_order, is_active)
                    SELECT @Id, @To, @Parent, reason_kind, utilization_class, code, name, oee_impact, is_selectable, requires_comment, sort_order, true
                    FROM core.reason_code WHERE id = @Source
                    """, new { Id = id, To = toSite, Parent = r.ParentId is { } p ? map.GetValueOrDefault(p, p) : (Guid?)null, Source = r.Id }, cancellationToken);
            }
        }

        var args = new { Src = map.Keys.ToArray(), Dst = map.Values.ToArray() };
        const string Map = "map AS (SELECT * FROM unnest(@Src::uuid[], @Dst::uuid[]) AS m(src, dst))";
        var profiles = 0;
        foreach (var profile in await IdsAsync("SELECT id FROM core.machine_profile WHERE site_id = @Site AND is_active ORDER BY code", fromSite, cancellationToken))
        {
            var id = newId();
            profiles++;
            await session.ExecuteAsync(
                $"""
                INSERT INTO core.machine_profile (id, site_id, code, name, vendor, model, description, is_active, created_at, updated_at)
                SELECT @Id, @To, code, name, vendor, model, description, true, @At, @At FROM core.machine_profile WHERE id = @Source;
                WITH {Map}
                INSERT INTO core.machine_profile_state (profile_id, raw_value, name, packml_state, reason_code_id, is_active)
                SELECT @Id, s.raw_value, s.name, s.packml_state, coalesce((SELECT dst FROM map WHERE src = s.reason_code_id), s.reason_code_id), true
                FROM core.machine_profile_state s WHERE s.profile_id = @Source AND s.is_active;
                WITH {Map}
                INSERT INTO core.machine_profile_code (profile_id, code_kind, raw_value, message, category, reason_code_id, is_active)
                SELECT @Id, c.code_kind, c.raw_value, c.message, c.category, coalesce((SELECT dst FROM map WHERE src = c.reason_code_id), c.reason_code_id), true
                FROM core.machine_profile_code c WHERE c.profile_id = @Source AND c.is_active;
                WITH {Map}
                INSERT INTO core.machine_profile_reason (profile_id, reason_code_id, is_offered)
                SELECT @Id, coalesce((SELECT dst FROM map WHERE src = r.reason_code_id), r.reason_code_id), r.is_offered
                FROM core.machine_profile_reason r WHERE r.profile_id = @Source;
                """, new { Id = id, To = toSite, Source = profile, At = at, args.Src, args.Dst }, cancellationToken);
        }

        return map.Count + profiles;
    }

    public async Task<int> CopyPeopleListsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var (table, columns) in new[]
        {
            ("core.crew", "code, name"),
            ("core.qualification", "code, name, description, validity_months, notice_days"),
            ("core.andon_type", "code, name, escalate_after_minutes, sort_order"),
        })
        {
            foreach (var id in await IdsAsync($"SELECT id FROM {table} WHERE site_id = @Site AND is_active ORDER BY code", fromSite, cancellationToken))
            {
                count += await session.ExecuteAsync(
                    $"""
                    INSERT INTO {table} (id, site_id, {columns}, is_active, created_at, updated_at)
                    SELECT @Id, @To, {columns}, true, @At, @At FROM {table} WHERE id = @Source
                    """, new { Id = newId(), To = toSite, Source = id, At = at }, cancellationToken);
            }
        }

        return count;
    }

    public async Task<int> CopySettingsAsync(Guid fromSite, Guid toSite, Func<Guid> newId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var args = new { From = fromSite, To = toSite, At = at };
        var count = await session.ExecuteAsync(
            """
            INSERT INTO core.scheduling_policy (site_id, changeover_mode, erp_batch_rule, updated_at)
            SELECT @To, changeover_mode, erp_batch_rule, @At FROM core.scheduling_policy WHERE site_id = @From;
            """, args, cancellationToken);
        count += await session.ExecuteAsync(
            """
            INSERT INTO core.execution_policy (site_id, rate_tolerance_pct, consumption_tolerance_pct, updated_at)
            SELECT @To, rate_tolerance_pct, consumption_tolerance_pct, @At FROM core.execution_policy WHERE site_id = @From;
            """, args, cancellationToken);
        count += await session.ExecuteAsync(
            """
            INSERT INTO core.rate_refresh_settings (site_id, take_out_stops, is_scheduled, every_days, window_days, threshold_pct, auto_accept, auto_accept_limit_pct, updated_at)
            SELECT @To, take_out_stops, is_scheduled, every_days, window_days, threshold_pct, auto_accept, auto_accept_limit_pct, @At FROM core.rate_refresh_settings WHERE site_id = @From;
            """, args, cancellationToken);
        count += await session.ExecuteAsync(
            """
            INSERT INTO core.allocation_settings (site_id, keep_current, hard_reservation, preferences, updated_at)
            SELECT @To, keep_current, hard_reservation, preferences, @At FROM core.allocation_settings WHERE site_id = @From;
            """, args, cancellationToken);
        count += await session.ExecuteAsync(
            """
            INSERT INTO core.release_approval_settings (site_id, processes, boms, specs, updated_at)
            SELECT @To, processes, boms, specs, @At FROM core.release_approval_settings WHERE site_id = @From;
            """, args, cancellationToken);

        foreach (var id in await IdsAsync("SELECT id FROM core.oee_target WHERE site_id = @Site AND equipment_id IS NULL AND effective_to IS NULL", fromSite, cancellationToken))
        {
            count += await session.ExecuteAsync(
                """
                INSERT INTO core.oee_target (id, site_id, equipment_id, effective_from, effective_to, availability_pct, performance_pct, quality_pct, oee_pct, created_at, updated_at)
                SELECT @Id, @To, NULL, @Today, NULL, availability_pct, performance_pct, quality_pct, oee_pct, @At, @At FROM core.oee_target WHERE id = @Source
                """, new { Id = newId(), To = toSite, Source = id, At = at, Today = DateOnly.FromDateTime(at.UtcDateTime).ToDateTime(TimeOnly.MinValue) }, cancellationToken);
        }

        foreach (var id in await IdsAsync("SELECT id FROM core.lot_attribute WHERE site_id = @Site AND is_active ORDER BY sort_order, code", fromSite, cancellationToken))
        {
            var attribute = newId();
            count += await session.ExecuteAsync(
                """
                INSERT INTO core.lot_attribute (id, site_id, code, name, data_type, uom_id, choices, applies_to_all, sort_order, is_active, created_at, updated_at)
                SELECT @Id, @To, code, name, data_type, uom_id, choices, applies_to_all, sort_order, true, @At, @At FROM core.lot_attribute WHERE id = @Source;
                INSERT INTO core.lot_attribute_class (attribute_id, material_class_id, is_applied)
                SELECT @Id, material_class_id, is_applied FROM core.lot_attribute_class WHERE attribute_id = @Source;
                """, new { Id = attribute, To = toSite, Source = id, At = at }, cancellationToken) > 0 ? 1 : 0;
        }

        return count;
    }

    private async Task<IReadOnlyList<Guid>> IdsAsync(string sql, Guid siteId, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<Guid>(sql, new { Site = siteId }, cancellationToken)];
}
