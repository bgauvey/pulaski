using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Common;
using Pulaski.Domain;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Capabilities with their run standards, and the CSV import (D-034): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class CapabilityAndImportTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<(Guid Line, Guid Department)> PlantAsync()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "ENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "SITE-" + _s, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        CreatedResponse area = await db.SendAsync(new CreateEquipmentCommand(site.Id, "AREA-" + _s, "Extrusion", null, "area", "area", false, true, false, null, CanRunJob: false));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(area.Id, "LN-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));
        return (line.Id, area.Id);
    }

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private Task<long> CapabilityVersionAsync(Guid id) =>
        ScalarAsync<long>("SELECT xmin::text::bigint FROM core.equipment_capability WHERE id = @Id", new { Id = id });

    [Fact]
    public async Task A_copied_line_brings_its_machines_capabilities_rules_and_targets_with_references_inside_it_pointing_at_the_copies()
    {
        var (line, area) = await PlantAsync();
        CreatedResponse saw = await db.SendAsync(new CreateEquipmentCommand(line, "LN-" + _s + "-SAW", "Saw", null, "work_unit", "machine", true, false, false, null));
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("CPY-" + _s, "Profile", null, "finished", null, db.UomId, true, null));
        await db.SendAsync(new AddCapabilityCommand(material.Id, line, 500m, db.UomId, 30, 2m, true, null, null));
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            await admin.ExecuteAsync(
                """
                UPDATE core.equipment SET constraint_equipment_id = @Saw, state_derivation = 'constraint_machine' WHERE id = @Line;
                INSERT INTO core.changeover_rule (id, site_id, equipment_id, to_material_id, duration, is_active, created_at, updated_at)
                SELECT uuidv7(), site_id, @Line, @Material, interval '20 minutes', true, now(), now() FROM core.equipment WHERE id = @Line;
                INSERT INTO core.oee_target (id, site_id, equipment_id, effective_from, oee_pct, created_at, updated_at)
                SELECT uuidv7(), site_id, @Line, current_date - 10, 75, now(), now() FROM core.equipment WHERE id = @Line;
                """, new { Saw = saw.Id, Line = line, Material = material.Id });
        }

        async Task<Contracts.EquipmentModel.LineCopyPlanDto> PlanAsync(string code)
        {
            await using var scope = db.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<Application.Abstractions.IQueryHandler<GetLineCopyPlanQuery, Contracts.EquipmentModel.LineCopyPlanDto>>()
                .HandleAsync(new GetLineCopyPlanQuery(line, code, "Line two"), CancellationToken.None);
        }

        // The review: children follow the new code; a code in use is a problem.
        var plan = await PlanAsync("LN2-" + _s);
        Assert.Equal(["LN2-" + _s, "LN2-" + _s + "-SAW"], plan.Items.Select(i => i.Code));
        Assert.Equal((1, 1, 1, 0), (plan.Capabilities, plan.ChangeoverRules, plan.OeeTargets, plan.TagsLeftOut));
        Assert.Empty(plan.Problems);
        Assert.NotEmpty((await PlanAsync("LN-" + _s)).Problems);

        var admin2 = new Domain.Security.UserAccess(Guid.CreateVersion7(), "test-user", "Test", "test-user", true,
            [new Domain.Security.RoleGrant(Guid.CreateVersion7(), Domain.Security.Roles.Administrator, "Administrator", null, [])]);

        var copied = await db.SendAsync(new CopyLineCommand(admin2, line, new Contracts.EquipmentModel.CopyLineRequest
        {
            Items = [.. plan.Items.Select(i => new Contracts.EquipmentModel.LineCopyNameDto(i.SourceId, i.Code, i.Name))],
        }));

        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            var (code, parent, constraint) = await admin.QuerySingleAsync<(string, Guid, Guid?)>(
                "SELECT code, parent_id, constraint_equipment_id FROM core.equipment WHERE id = @Id", new { Id = copied.Id });
            var newSaw = await admin.ExecuteScalarAsync<Guid>("SELECT id FROM core.equipment WHERE parent_id = @Id", new { Id = copied.Id });
            Assert.Equal(("LN2-" + _s, area, (Guid?)newSaw), (code, parent, constraint));
            Assert.Equal(2, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM core.equipment_closure WHERE descendant_id = @Saw AND depth > 0 AND ancestor_id IN (@Line, @Area)",
                new { Saw = newSaw, Line = copied.Id, Area = area }));
            Assert.Equal((500m, "ideal_rate"), await admin.QuerySingleAsync<(decimal, string)>(
                "SELECT calculated_rate_per_hour, calculation_basis FROM core.run_standard WHERE equipment_id = @Id AND material_id = @M", new { Id = copied.Id, M = material.Id }));
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM core.equipment_capability WHERE equipment_id = @Id AND effective_to IS NULL", new { Id = copied.Id }));
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM core.changeover_rule WHERE equipment_id = @Id AND is_active", new { Id = copied.Id }));
            Assert.Equal(75m, await admin.ExecuteScalarAsync<decimal>("SELECT oee_pct FROM core.oee_target WHERE equipment_id = @Id", new { Id = copied.Id }));
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM evt.event WHERE subject = @Id AND type = 'pulaski.equipment.line_copied.v1'", new { Id = copied.Id }));
        }

        // The same codes again: refused.
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CopyLineCommand(admin2, line, new Contracts.EquipmentModel.CopyLineRequest
        {
            Items = [.. plan.Items.Select(i => new Contracts.EquipmentModel.LineCopyNameDto(i.SourceId, i.Code, i.Name))],
        })));
    }

    [Fact]
    public async Task A_new_site_takes_the_reference_data_of_another_with_its_reasons_and_profiles_pointing_at_the_copies()
    {
        var (line, _) = await PlantAsync();
        Guid site, enterprise;
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            (site, enterprise) = await admin.QuerySingleAsync<(Guid, Guid)>(
                "SELECT s.id, s.parent_id FROM core.equipment l JOIN core.equipment s ON s.id = l.site_id WHERE l.id = @Line", new { Line = line });
            await admin.ExecuteAsync(
                """
                INSERT INTO core.shift_schedule (id, site_id, code, name, cycle_days, cycle_start, shifts, overrides)
                VALUES (uuidv7(), @Site, 'S3X8', 'Three shifts', 1, current_date, '[{"code":"DAY","crew":"A","days":[0],"start":"06:00","minutes":480}]', '[{"date":"2026-12-25","off":true}]');
                INSERT INTO core.crew (id, site_id, code, name) VALUES (uuidv7(), @Site, 'A', 'Crew A');
                INSERT INTO core.qualification (id, site_id, code, name) VALUES (uuidv7(), @Site, 'FORK', 'Forklift');
                INSERT INTO core.andon_type (id, site_id, code, name, escalate_after_minutes, sort_order) VALUES (uuidv7(), @Site, 'MAINT', 'Maintenance', 10, 1);
                INSERT INTO core.reason_code (id, site_id, parent_id, reason_kind, code, name, is_selectable, sort_order)
                VALUES (uuidv7(), @Site, NULL, 'utilization', 'G-' || @S, 'Mechanical', false, 1);
                INSERT INTO core.reason_code (id, site_id, parent_id, reason_kind, utilization_class, code, name, is_selectable, sort_order)
                SELECT uuidv7(), @Site, id, 'utilization', 'downtime', 'DIE-' || @S, 'Die', true, 2 FROM core.reason_code WHERE code = 'G-' || @S AND site_id = @Site;
                INSERT INTO core.machine_profile (id, site_id, code, name) VALUES (uuidv7(), @Site, 'P-' || @S, 'Extruder');
                INSERT INTO core.machine_profile_state (profile_id, raw_value, name, packml_state, reason_code_id)
                SELECT p.id, 9, 'Die change', 6, r.id FROM core.machine_profile p, core.reason_code r WHERE p.code = 'P-' || @S AND r.code = 'DIE-' || @S;
                INSERT INTO core.execution_policy (site_id, rate_tolerance_pct, consumption_tolerance_pct) VALUES (@Site, 125, 115);
                INSERT INTO core.oee_target (id, site_id, effective_from, oee_pct) VALUES (uuidv7(), @Site, current_date - 30, 80);
                INSERT INTO core.lot_attribute (id, site_id, code, name, data_type, applies_to_all, sort_order) VALUES (uuidv7(), @Site, 'MFI', 'Melt index', 'number', true, 1);
                """, new { Site = site, S = _s });
        }

        async Task<Contracts.EquipmentModel.SiteCopyPlanDto> PlanAsync()
        {
            await using var scope = db.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<Application.Abstractions.IQueryHandler<GetSiteCopyPlanQuery, Contracts.EquipmentModel.SiteCopyPlanDto>>()
                .HandleAsync(new GetSiteCopyPlanQuery(site), CancellationToken.None);
        }

        var plan = await PlanAsync();
        Assert.Equal((1, 2, 1, 1, 1, 1, 1, 2), (plan.ShiftSchedules, plan.Reasons, plan.MachineProfiles, plan.Crews, plan.Qualifications, plan.AndonTypes, plan.LotAttributes, plan.Settings));

        var admin2 = new Domain.Security.UserAccess(Guid.CreateVersion7(), "test-user", "Test", "test-user", true,
            [new Domain.Security.RoleGrant(Guid.CreateVersion7(), Domain.Security.Roles.Administrator, "Administrator", null, [])]);
        var copied = await db.SendAsync(new CopySiteCommand(admin2, site, new Contracts.EquipmentModel.CopySiteRequest
        {
            Code = "SITE2-" + _s,
            Name = "Second plant",
            TimeZone = "Europe/Berlin",
        }));
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new CopySiteCommand(admin2, site, new Contracts.EquipmentModel.CopySiteRequest { Code = "X" + _s, Name = "X" })));

        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            var (parent, zone, level) = await admin.QuerySingleAsync<(Guid, string, string)>("SELECT parent_id, time_zone, level FROM core.equipment WHERE id = @Id", new { Id = copied.Id });
            Assert.Equal((enterprise, "Europe/Berlin", "site"), (parent, zone, level));
            Assert.Equal("[]", await admin.ExecuteScalarAsync<string>("SELECT overrides::text FROM core.shift_schedule WHERE site_id = @Id AND code = 'S3X8'", new { Id = copied.Id }));
            Assert.Equal(3, await admin.ExecuteScalarAsync<int>(
                "SELECT (SELECT count(*) FROM core.crew WHERE site_id = @Id) + (SELECT count(*) FROM core.qualification WHERE site_id = @Id) + (SELECT count(*) FROM core.andon_type WHERE site_id = @Id)",
                new { Id = copied.Id }));

            // The member's group and the profile's state point at the new site's copies, not the original's.
            var (dieParentSite, stateReasonSite) = await admin.QuerySingleAsync<(Guid, Guid)>(
                """
                SELECT g.site_id, sr.site_id
                FROM core.reason_code d JOIN core.reason_code g ON g.id = d.parent_id,
                     core.machine_profile p JOIN core.machine_profile_state ps ON ps.profile_id = p.id JOIN core.reason_code sr ON sr.id = ps.reason_code_id
                WHERE d.site_id = @Id AND d.code = 'DIE-' || @S AND p.site_id = @Id
                """, new { Id = copied.Id, S = _s });
            Assert.Equal((copied.Id, copied.Id), (dieParentSite, stateReasonSite));
            Assert.Equal(125m, await admin.ExecuteScalarAsync<decimal>("SELECT rate_tolerance_pct FROM core.execution_policy WHERE site_id = @Id", new { Id = copied.Id }));
            Assert.Equal(80m, await admin.ExecuteScalarAsync<decimal>("SELECT oee_pct FROM core.oee_target WHERE site_id = @Id AND equipment_id IS NULL", new { Id = copied.Id }));
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM core.lot_attribute WHERE site_id = @Id", new { Id = copied.Id }));
        }
    }

    [Fact]
    public async Task A_line_copied_into_another_site_takes_that_sites_own_by_code_and_goes_without_what_it_lacks()
    {
        var (line, _) = await PlantAsync();
        Guid site;
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            site = await admin.ExecuteScalarAsync<Guid>("SELECT site_id FROM core.equipment WHERE id = @Line", new { Line = line });
            await admin.ExecuteAsync(
                """
                INSERT INTO core.machine_profile (id, site_id, code, name) VALUES (uuidv7(), @Site, 'PX-' || @S, 'Extruder');
                INSERT INTO core.qualification (id, site_id, code, name) VALUES (uuidv7(), @Site, 'QX-' || @S, 'Extruder operator');
                INSERT INTO core.label_printer (id, site_id, code, name, method, drop_folder, template_set) VALUES (uuidv7(), @Site, 'LP' || @S, 'Line printer', 'drop_folder', 'line', 'default');
                UPDATE core.equipment SET machine_profile_id = (SELECT id FROM core.machine_profile WHERE code = 'PX-' || @S),
                                          label_printer_id = (SELECT id FROM core.label_printer WHERE code = 'LP' || @S) WHERE id = @Line;
                INSERT INTO core.equipment_qualification (equipment_id, qualification_id, is_required, updated_at)
                SELECT @Line, id, true, now() FROM core.qualification WHERE code = 'QX-' || @S;
                """, new { Site = site, S = _s, Line = line });
        }

        var admin2 = new Domain.Security.UserAccess(Guid.CreateVersion7(), "test-user", "Test", "test-user", true,
            [new Domain.Security.RoleGrant(Guid.CreateVersion7(), Domain.Security.Roles.Administrator, "Administrator", null, [])]);
        // A second site with the first's lists (its profile and qualification, by code), but not its printer.
        var second = await db.SendAsync(new CopySiteCommand(admin2, site, new Contracts.EquipmentModel.CopySiteRequest
        {
            Code = "SITE3-" + _s,
            Name = "Third plant",
            TimeZone = "America/Chicago",
        }));

        Contracts.EquipmentModel.LineCopyPlanDto plan;
        await using (var scope = db.Services.CreateAsyncScope())
        {
            plan = await scope.ServiceProvider.GetRequiredService<Application.Abstractions.IQueryHandler<GetLineCopyPlanQuery, Contracts.EquipmentModel.LineCopyPlanDto>>()
                .HandleAsync(new GetLineCopyPlanQuery(line, "LN-" + _s, "Line", second.Id), CancellationToken.None);
        }

        // The same code is free at the other site; only the printer has no match there.
        Assert.Equal((second.Id, 0), (plan.TargetSiteId, plan.Problems.Count));
        Assert.Contains("LP" + _s, Assert.Single(plan.Unmatched).Text, StringComparison.Ordinal);

        var copied = await db.SendAsync(new CopyLineCommand(admin2, line, new Contracts.EquipmentModel.CopyLineRequest
        {
            ParentId = second.Id,
            Items = [.. plan.Items.Select(i => new Contracts.EquipmentModel.LineCopyNameDto(i.SourceId, i.Code, i.Name))],
        }));

        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            var (siteId, profileSite, printer) = await admin.QuerySingleAsync<(Guid, Guid?, Guid?)>(
                """
                SELECT e.site_id, p.site_id, e.label_printer_id FROM core.equipment e LEFT JOIN core.machine_profile p ON p.id = e.machine_profile_id WHERE e.id = @Id
                """, new { Id = copied.Id });
            Assert.Equal((second.Id, (Guid?)second.Id, (Guid?)null), (siteId, profileSite, printer));
            Assert.Equal(second.Id, await admin.ExecuteScalarAsync<Guid>(
                "SELECT q.site_id FROM core.equipment_qualification eq JOIN core.qualification q ON q.id = eq.qualification_id WHERE eq.equipment_id = @Id", new { Id = copied.Id }));
        }
    }

    [Fact]
    public async Task The_audit_trail_says_who_changed_a_capability_and_what_changed()
    {
        var (line, _) = await PlantAsync();
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("AUD-" + _s, "Profile", null, "finished", null, db.UomId, true, null));
        var added = await db.SendAsync(new AddCapabilityCommand(material.Id, line, 500m, db.UomId, 30, 2m, true, null, null));
        var from = await ScalarAsync<DateTimeOffset>("SELECT effective_from FROM core.equipment_capability WHERE id = @Id", new { Id = added.Id });
        await db.SendAsAsync("pat." + _s, new UpdateCapabilityCommand(added.Id, added.Version, 550m, db.UomId, 30, 2m, true, from, null));

        async Task<Contracts.Audit.AuditPageDto> SearchAsync(Application.Audit.AuditFilter filter)
        {
            await using var scope = db.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<Application.Abstractions.IQueryHandler<Application.Audit.SearchAuditQuery, Contracts.Audit.AuditPageDto>>()
                .HandleAsync(new Application.Audit.SearchAuditQuery(filter), CancellationToken.None);
        }

        // The capability's history, newest first: changed by pat (the rate, 500 → 550), added by the test user.
        var history = await SearchAsync(new Application.Audit.AuditFilter(SubjectId: added.Id));
        Assert.Equal(["capability changed", "capability added"], history.Events.Select(e => e.Action));
        var change = history.Events[0];
        Assert.Equal(("pat." + _s, "equipment_capability"), (change.ActorId, change.SubjectType));
        Assert.Equal([("idealRatePerHour", "500", "550")], change.Changes.Select(c => (c.Field, c.Before, c.After)));
        Assert.DoesNotContain(change.Changes, c => c.Field == "materialId");
        Assert.NotEmpty(history.Events[1].Fields);

        // Found by who and by the material's id in the change, a page at a time.
        var byPat = await SearchAsync(new Application.Audit.AuditFilter(Actor: "PAT." + _s));
        // Pat's change also recalculated the run standard: both are theirs, nothing else is.
        Assert.Equal(["equipment_capability", "run_standard"], byPat.Events.Select(e => e.SubjectType).Order());
        var first = await SearchAsync(new Application.Audit.AuditFilter(Area: "equipment_capability", Text: material.Id.ToString(), Limit: 1));
        Assert.Equal(change.Id, Assert.Single(first.Events).Id);
        var cursor = Application.Audit.AuditCursor.Parse(first.NextBefore)!.Value;
        var second = await SearchAsync(new Application.Audit.AuditFilter(Area: "equipment_capability", Text: material.Id.ToString(), BeforeRecordedAt: cursor.RecordedAt, BeforeId: cursor.Id, Limit: 1));
        Assert.Equal(("capability added", (string?)null), (Assert.Single(second.Events).Action, second.NextBefore));
        await Assert.ThrowsAsync<ValidationException>(() => SearchAsync(new Application.Audit.AuditFilter(From: DateTimeOffset.UtcNow.AddDays(-100))));
    }

    [Fact]
    public async Task A_capability_seeds_the_run_standard_follows_its_ideal_rate_and_ends_without_being_deleted()
    {
        var (line, department) = await PlantAsync();
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("CAP-" + _s, "Profile", null, "finished", null, db.UomId, true, null));

        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new AddCapabilityCommand(material.Id, department, 500m, db.UomId, null, null, false, null, null)));
        var added = await db.SendAsync(new AddCapabilityCommand(material.Id, line, 500m, db.UomId, 30, 2m, true, null, null));
        Assert.Contains("Planning rate set to 500", Assert.Single(added.Notes).Text, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new AddCapabilityCommand(material.Id, line, 600m, db.UomId, null, null, false, null, null)));

        var standardId = await ScalarAsync<Guid>("SELECT id FROM core.run_standard WHERE equipment_id = @Line AND material_id = @M", new { Line = line, M = material.Id });
        var standardVersion = await ScalarAsync<long>("SELECT xmin::text::bigint FROM core.run_standard WHERE id = @Id", new { Id = standardId });
        await db.SendAsync(new ModifyRunStandardCommand(standardId, standardVersion, 450m, "Trial"));

        var from = await ScalarAsync<DateTimeOffset>("SELECT effective_from FROM core.equipment_capability WHERE id = @Id", new { Id = added.Id });
        var changed = await db.SendAsync(new UpdateCapabilityCommand(added.Id, added.Version, 550m, db.UomId, 30, 2m, true, from, null));
        Assert.Contains("modified rate of 450", Assert.Single(changed.Notes).Text, StringComparison.Ordinal);
        Assert.Equal((550m, 450m), (
            await ScalarAsync<decimal>("SELECT calculated_rate_per_hour FROM core.run_standard WHERE id = @Id", new { Id = standardId }),
            await ScalarAsync<decimal>("SELECT effective_rate_per_hour FROM core.run_standard WHERE id = @Id", new { Id = standardId })));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new UpdateCapabilityCommand(added.Id, added.Version, 560m, db.UomId, 30, 2m, true, from, null)));

        await db.SendAsync(new EndCapabilityCommand(added.Id, await CapabilityVersionAsync(added.Id)));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*)::int FROM core.equipment_capability WHERE id = @Id AND effective_to IS NOT NULL", new { Id = added.Id }));
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new EndCapabilityCommand(added.Id, await CapabilityVersionAsync(added.Id))));
        var again = await db.SendAsync(new AddCapabilityCommand(material.Id, line, 520m, db.UomId, null, null, false, null, null));
        Assert.Contains("Calculated rate now 520", Assert.Single(again.Notes).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_csv_check_saves_nothing_and_an_import_with_an_error_saves_nothing_either()
    {
        var unit = await ScalarAsync<string>("SELECT code FROM core.uom WHERE id = @Id", new { Id = db.UomId });
        var good = $"code,name,type,base_unit,shelf_life_days\nCSV1-{_s},One,raw,{unit},30\nCSV2-{_s},Two,raw,{unit},\n";
        var count = () => ScalarAsync<int>("SELECT count(*)::int FROM core.material WHERE code LIKE @P", new { P = "CSV_-" + _s });

        var check = await db.SendAsync(new ImportMaterialsCommand(MaterialCsv.Parse(good), DryRun: true, "CSV import"));
        Assert.Equal((false, 2, 0), (check.Applied, check.Created, await count()));

        var bad = await db.SendAsync(new ImportMaterialsCommand(MaterialCsv.Parse(good + $"CSV3-{_s},Three,raw,nonesuch,\n"), DryRun: false, "CSV import"));
        Assert.Equal((false, 1, 0), (bad.Applied, bad.Errors, await count()));

        var applied = await db.SendAsync(new ImportMaterialsCommand(MaterialCsv.Parse(good), DryRun: false, "CSV import"));
        Assert.Equal((true, 2), (applied.Applied, await count()));
        Assert.Equal(2, await ScalarAsync<int>(
            "SELECT count(*)::int FROM evt.event WHERE type = 'pulaski.material.created.v1' AND reason = 'CSV import' AND data->'after'->>'code' LIKE @P",
            new { P = "CSV_-" + _s }));
    }

    [Fact]
    public async Task Capabilities_and_new_classes_import_all_or_nothing_with_the_lines_locked_by_code()
    {
        var (line, _) = await PlantAsync();
        var unit = await ScalarAsync<string>("SELECT code FROM core.uom WHERE id = @Id", new { Id = db.UomId });
        var materials = $"code,name,type,base_unit,class,class_name\nCI-{_s},Profile,finished,{unit},CL-{_s},Profiles\n";

        var check = await db.SendAsync(new ImportMaterialsCommand(MaterialCsv.Parse(materials), DryRun: true, "CSV import", CreateClasses: true));
        Assert.Equal([$"CL-{_s}"], check.CreatedClasses);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*)::int FROM core.material_class WHERE code = @C", new { C = $"CL-{_s}" }));
        var applied = await db.SendAsync(new ImportMaterialsCommand(MaterialCsv.Parse(materials), DryRun: false, "CSV import", CreateClasses: true));
        Assert.True(applied.Applied);
        Assert.Equal("Profiles", await ScalarAsync<string>("SELECT name FROM core.material_class WHERE code = @C", new { C = $"CL-{_s}" }));

        var good = $"material,line,ideal_rate,rate_unit,crew_size\nCI-{_s},ln-{_s},480.5,{unit},2\n";
        var bad = await db.SendAsync(new ImportCapabilitiesCommand(CapabilityCsv.Parse(good + $"CI-{_s},NOPE-{_s},1,{unit},\n"), false, "CSV import", null));
        Assert.Equal((false, 1), (bad.Applied, bad.Errors));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*)::int FROM core.equipment_capability WHERE equipment_id = @Line", new { Line = line }));

        var added = await db.SendAsync(new ImportCapabilitiesCommand(CapabilityCsv.Parse(good), false, "CSV import", null));
        Assert.Equal((true, 1), (added.Applied, added.Created));
        Assert.Equal(480.5m, await ScalarAsync<decimal>("SELECT calculated_rate_per_hour FROM core.run_standard WHERE equipment_id = @Line", new { Line = line }));

        var changed = await db.SendAsync(new ImportCapabilitiesCommand(
            CapabilityCsv.Parse($"material,line,ideal_rate,rate_unit\nCI-{_s},LN-{_s},500,{unit}\n"), false, "CSV import", null));
        Assert.Equal(["ideal_rate"], Assert.Single(changed.Rows).Changes);
        Assert.Equal((500m, 2m), (
            await ScalarAsync<decimal>("SELECT ideal_rate_per_hour FROM core.equipment_capability WHERE equipment_id = @Line", new { Line = line }),
            await ScalarAsync<decimal>("SELECT standard_crew_size FROM core.equipment_capability WHERE equipment_id = @Line", new { Line = line })));

        var elsewhere = await db.SendAsync(new ImportCapabilitiesCommand(CapabilityCsv.Parse(good), true, "CSV import", [Guid.CreateVersion7()]));
        Assert.Contains("can't change the capabilities", Assert.Single(elsewhere.Rows).Message?.Text, StringComparison.Ordinal);
    }
}
