using Pulaski.Application.Materials;
using Pulaski.Domain.Materials;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Materials;

/// <summary>Bulk import of materials, all or nothing (D-034).</summary>
public class ImportMaterialsHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _kg;

    public ImportMaterialsHandlerTests()
    {
        _kg = Admin.AddUom("kg");
        Admin.AddUom("m");
    }

    private InMemoryMaterialAdmin Admin => _f.Stores.MaterialAdmin;

    private Task<Contracts.Materials.ImportMaterialsResponse> RunAsync(string csv, bool dryRun = false) =>
        new ImportMaterialsHandler(_f.Stores.UnitOfWork, Admin, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new ImportMaterialsCommand(MaterialCsv.Parse(csv), dryRun, "CSV import"), CancellationToken.None);

    private Material ByCode(string code) => Admin.FindByCodeForUpdateAsync(code, CancellationToken.None).Result!.Value;

    [Fact]
    public async Task New_rows_are_created_and_known_ones_updated_keeping_what_the_file_leaves_out()
    {
        var resins = Admin.AddClass("RESIN");
        var first = await RunAsync("code,name,type,base_unit,class,shelf_life_days,lot_tracked\nPP-1,Polypropylene,raw,kg,resin,180,no\n");
        Assert.Equal((true, 1), (first.Applied, first.Created));
        Assert.Equal((resins.Id, 180, false), (ByCode("PP-1").MaterialClassId, ByCode("PP-1").ShelfLifeDays, ByCode("PP-1").IsLotTracked));

        var second = await RunAsync("code,name,type,base_unit\nPP-1,PP homopolymer,raw,kg\n");
        var row = Assert.Single(second.Rows);
        Assert.Equal("update", row.Action);
        Assert.Equal(["name"], row.Changes);
        Assert.Equal((resins.Id, 180, false), (ByCode("PP-1").MaterialClassId, ByCode("PP-1").ShelfLifeDays, ByCode("PP-1").IsLotTracked));

        var again = await RunAsync("code,name,type,base_unit\nPP-1,PP homopolymer,raw,kg\n");
        Assert.Equal(("unchanged", 1), (Assert.Single(again.Rows).Action, again.Unchanged));
        Assert.Equal("CSV import", _f.Stores.Events.Appended[^1].Reason);
    }

    [Fact]
    public async Task Erp_rows_match_on_their_key_and_set_pulaskis_fields_too()
    {
        await RunAsync("code,name,type,base_unit,external_system,external_key,shelf_life_days\nR-1,Resin,raw,kg,ERP1,77,90\n");
        var created = ByCode("R-1");
        Assert.Equal((MaterialOrigin.Erp, "77", 90), (created.Origin, created.ExternalKey, created.ShelfLifeDays));

        var renamed = await RunAsync("code,name,type,base_unit,external_system,external_key\nR-1B,Resin B,raw,kg,ERP1,77\n");
        Assert.Equal(["code", "name"], Assert.Single(renamed.Rows).Changes);
        Assert.Equal((created.Id, 90), (ByCode("R-1B").Id, ByCode("R-1B").ShelfLifeDays));

        var asLocal = await RunAsync("code,name,type,base_unit\nR-1B,Resin B,raw,kg\n");
        Assert.Contains("comes from ERP1", Assert.Single(asLocal.Rows).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_bad_row_stops_the_whole_import_and_every_problem_is_listed()
    {
        Admin.AddClass("OLD", isActive: false);
        var result = await RunAsync(
            "code,name,type,base_unit,class,external_system,external_key\n" +
            "A,A,raw,kg,,,\n" +
            "B,B,gizmo,kg,,,\n" +
            "C,C,raw,furlong,,,\n" +
            "D,D,raw,kg,OLD,,\n" +
            "E,E,raw,kg,,ERP1,\n" +
            "A,A again,raw,kg,,,\n");

        Assert.False(result.Applied);
        Assert.Equal((1, 5), (result.Created, result.Errors));
        Assert.Equal(["create", "error", "error", "error", "error", "error"], result.Rows.Select(r => r.Action));
        Assert.Contains("appears earlier", result.Rows[^1].Message, StringComparison.Ordinal);
        Assert.Equal(0, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task A_dry_run_reports_without_committing()
    {
        var result = await RunAsync("code,name,type,base_unit\nX,X,raw,kg\n", dryRun: true);

        Assert.Equal((false, 1, 0), (result.Applied, result.Created, _f.Stores.UnitOfWork.Committed));
    }
}
