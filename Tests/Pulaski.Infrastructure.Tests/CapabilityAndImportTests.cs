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
    public async Task A_capability_seeds_the_run_standard_follows_its_ideal_rate_and_ends_without_being_deleted()
    {
        var (line, department) = await PlantAsync();
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("CAP-" + _s, "Profile", null, "finished", null, db.UomId, true, null));

        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new AddCapabilityCommand(material.Id, department, 500m, db.UomId, null, null, false, null, null)));
        var added = await db.SendAsync(new AddCapabilityCommand(material.Id, line, 500m, db.UomId, 30, 2m, true, null, null));
        Assert.Contains("Planning rate set to 500", Assert.Single(added.Notes), StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new AddCapabilityCommand(material.Id, line, 600m, db.UomId, null, null, false, null, null)));

        var standardId = await ScalarAsync<Guid>("SELECT id FROM core.run_standard WHERE equipment_id = @Line AND material_id = @M", new { Line = line, M = material.Id });
        var standardVersion = await ScalarAsync<long>("SELECT xmin::text::bigint FROM core.run_standard WHERE id = @Id", new { Id = standardId });
        await db.SendAsync(new ModifyRunStandardCommand(standardId, standardVersion, 450m, "Trial"));

        var from = await ScalarAsync<DateTimeOffset>("SELECT effective_from FROM core.equipment_capability WHERE id = @Id", new { Id = added.Id });
        var changed = await db.SendAsync(new UpdateCapabilityCommand(added.Id, added.Version, 550m, db.UomId, 30, 2m, true, from, null));
        Assert.Contains("modified rate of 450", Assert.Single(changed.Notes), StringComparison.Ordinal);
        Assert.Equal((550m, 450m), (
            await ScalarAsync<decimal>("SELECT calculated_rate_per_hour FROM core.run_standard WHERE id = @Id", new { Id = standardId }),
            await ScalarAsync<decimal>("SELECT effective_rate_per_hour FROM core.run_standard WHERE id = @Id", new { Id = standardId })));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new UpdateCapabilityCommand(added.Id, added.Version, 560m, db.UomId, 30, 2m, true, from, null)));

        await db.SendAsync(new EndCapabilityCommand(added.Id, await CapabilityVersionAsync(added.Id)));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*)::int FROM core.equipment_capability WHERE id = @Id AND effective_to IS NOT NULL", new { Id = added.Id }));
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new EndCapabilityCommand(added.Id, await CapabilityVersionAsync(added.Id))));
        var again = await db.SendAsync(new AddCapabilityCommand(material.Id, line, 520m, db.UomId, null, null, false, null, null));
        Assert.Contains("Calculated rate now 520", Assert.Single(again.Notes), StringComparison.Ordinal);
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
        Assert.Contains("can't change the capabilities", Assert.Single(elsewhere.Rows).Message, StringComparison.Ordinal);
    }
}
