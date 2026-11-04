using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Materials;

/// <summary>Material connections (D-068): OData items into import rows, and the rules for setting one up and running it.</summary>
public class MaterialConnectionTests
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["code"] = "ProductNumber",
        ["name"] = "Name",
        ["base_unit"] = "Unit/Code",
        ["type"] = "Kind",
        ["lot_tracked"] = "Batched",
        ["shelf_life_days"] = "ShelfLife",
        ["description"] = "Text",
        ["external_key"] = "ProductID",
    };

    private static MaterialConnection Connection(IReadOnlyDictionary<string, string>? map = null) =>
        new(Guid.CreateVersion7(), "ERP-ITEMS", "ERP items", "odata", "https://erp.example/odata", "Products", null, "ERP1", map ?? Map, true);

    private static IReadOnlyList<JsonElement> Items(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    [Fact]
    public void Items_become_rows_with_the_connections_system_their_key_and_values_read_as_any_source_reads_them()
    {
        var source = ODataMaterialRows.Read(Items("""
            [
              {"ProductID": 1001, "ProductNumber": " PVC-K67 ", "Name": "PVC compound", "Unit": {"Code": "kg"}, "Kind": "raw",
               "Batched": true, "ShelfLife": 180, "Text": null},
              {"ProductID": "1002", "ProductNumber": "CAP-1", "Name": "End cap", "Unit": {"Code": "ea"}, "Kind": "purchased", "Batched": false,
               "ShelfLife": "soon", "Text": {"en": "x"}}
            ]
            """), Connection());

        var first = source.Rows[0];
        Assert.Equal(("PVC-K67", "PVC compound", "kg", "raw", "ERP1", "1001"), (first.Code, first.Name, first.BaseUomCode, first.MaterialType, first.ExternalSystem!, first.ExternalKey!));
        Assert.Equal((true, 180), (first.IsLotTracked!.Value, first.ShelfLifeDays!.Value!.Value));
        Assert.Null(first.Description!.Value);
        Assert.Empty(first.Problems);

        // A value that isn't one, or doesn't read, is the row's problem; the import then refuses the row.
        var second = source.Rows[1];
        Assert.Contains(second.Problems, p => p.Contains("description: Text isn't a single value", StringComparison.Ordinal));
        Assert.Contains(second.Problems, p => p.Contains("shelf_life_days", StringComparison.Ordinal));
        Assert.Equal(2, second.Line);
    }

    [Fact]
    public void A_map_names_only_import_fields_as_property_names_and_always_maps_code_and_key()
    {
        Assert.Empty(ODataMaterialRows.Problems(Map));
        var problems = ODataMaterialRows.Problems(new Dictionary<string, string> { ["colour"] = "Colour", ["name"] = "Name; drop" });
        Assert.Contains(problems, p => p.Contains("'colour' isn't an import field", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("isn't a property name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Map code", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Map external_key", StringComparison.Ordinal));
        Assert.Equal(["Batched", "Kind", "Name", "ProductID", "ProductNumber", "ShelfLife", "Text", "Unit"], ODataMaterialRows.Selected(Map));
    }

    [Fact]
    public async Task A_connection_is_set_up_with_a_secure_address_and_keeps_its_code_and_a_check_records_nothing()
    {
        var f = new HandlerFixture();
        var store = f.Stores.MaterialConnections;
        var save = new SaveMaterialConnectionHandler(f.Stores.UnitOfWork, store, f.Stores.Events, f.Events, f.Ids);
        SaveMaterialConnectionRequest Request(string url = "https://erp.example/odata/", string code = "erp-items") => new()
        {
            Code = code,
            Name = "ERP items",
            BaseUrl = url,
            EntitySet = "Products",
            ExternalSystem = "ERP1",
            FieldMap = Map,
        };

        await Assert.ThrowsAsync<ValidationException>(() => save.HandleAsync(new SaveMaterialConnectionCommand(null, Request("http://erp.example/odata")), CancellationToken.None));
        var created = await save.HandleAsync(new SaveMaterialConnectionCommand(null, Request()), CancellationToken.None);
        var saved = (await store.GetForUpdateAsync(created.Id, CancellationToken.None))!;
        Assert.Equal(("ERP-ITEMS", "https://erp.example/odata"), (saved.Value.Code, saved.Value.BaseUrl));
        await Assert.ThrowsAsync<DomainRuleException>(() => save.HandleAsync(
            new SaveMaterialConnectionCommand(created.Id, Request(code: "OTHER") with { Version = saved.Version }), CancellationToken.None));
        Assert.Equal(EventTypes.MaterialConnectionAdded, f.Stores.Events.Appended[^1].Type);

        // A fetch that fails is the import's problem, and only an import (not a check) is recorded.
        f.Stores.MaterialConnectionFetcher.Problems["ERP-ITEMS"] = "ERP-ITEMS: the service answered 401.";
        var run = new RunMaterialConnectionHandler(
            f.Stores.UnitOfWork, store, f.Stores.MaterialConnectionFetcher, new ThrowingImport(), new FixedRequestContext("ops"), f.Time);
        var checkResult = await run.HandleAsync(new RunMaterialConnectionCommand(created.Id, DryRun: true, CreateClasses: false), CancellationToken.None);
        Assert.Equal(["ERP-ITEMS: the service answered 401."], checkResult.FileProblems);
        Assert.Empty(await store.LastRunsAsync(CancellationToken.None));
        await run.HandleAsync(new RunMaterialConnectionCommand(created.Id, DryRun: false, CreateClasses: false), CancellationToken.None);
        Assert.Equal("ops", (await store.LastRunsAsync(CancellationToken.None))[created.Id].By);
    }

    // The import isn't reached when the fetch fails.
    private sealed class ThrowingImport : ICommandHandler<ImportMaterialsCommand, ImportMaterialsResponse>
    {
        public Task<ImportMaterialsResponse> HandleAsync(ImportMaterialsCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");
    }

    [Fact]
    public void Each_kind_needs_its_own_fields_and_only_odata_takes_a_filter()
    {
        var odata = Connection();
        Assert.Empty(MaterialConnectionRules.KindProblems(odata));

        var rest = odata with { Kind = "rest", BaseUrl = "https://erp.example/api/items?plant=1", EntitySet = "", NextLinkPath = "links.next" };
        Assert.Empty(MaterialConnectionRules.KindProblems(rest));
        Assert.Contains(MaterialConnectionRules.KindProblems(rest with { Filter = "x eq 1" }), p => p.StartsWith("Filter", StringComparison.Ordinal));
        Assert.Contains(MaterialConnectionRules.KindProblems(rest with { NextLinkPath = "links next" }), p => p.StartsWith("NextLinkPath", StringComparison.Ordinal));

        var soap = odata with { Kind = "soap", EntitySet = "Material", RequestBody = "<Envelope/>" };
        Assert.Empty(MaterialConnectionRules.KindProblems(soap));
        Assert.Contains(MaterialConnectionRules.KindProblems(soap with { RequestBody = "GetMaterials" }), p => p.StartsWith("RequestBody", StringComparison.Ordinal));

        var database = odata with { Kind = "database", BaseUrl = null, EntitySet = "", Query = "WITH i AS (SELECT 1 AS n) SELECT n FROM i;" };
        Assert.Empty(MaterialConnectionRules.KindProblems(database));
        Assert.NotEmpty(MaterialConnectionRules.KindProblems(database with { Query = "DELETE FROM erp.items" }));
        Assert.NotEmpty(MaterialConnectionRules.KindProblems(database with { Query = "SELECT 1; DROP TABLE erp.items" }));
    }

    [Fact]
    public void Rest_and_soap_answers_and_database_rows_become_items_the_field_map_reads()
    {
        using var answer = JsonDocument.Parse("""{"data": {"items": [{"No": "A"}]}, "next": "?page=2"}""");
        Assert.Equal("A", Assert.Single(ConnectionItems.Rest(answer.RootElement, "data/items").Items!).GetProperty("No").GetString());
        Assert.Equal("?page=2", ConnectionItems.NextLink(answer.RootElement, "next"));
        Assert.Null(ConnectionItems.NextLink(answer.RootElement, null));
        Assert.Contains("isn't an array", ConnectionItems.Rest(answer.RootElement, "").Problem, StringComparison.Ordinal);

        var (items, problem) = ConnectionItems.Soap("<r><Item><No>A</No><No>ignored</No><Empty/></Item><Item><No>B</No></Item></r>", "Item");
        Assert.Null(problem);
        Assert.Equal(["A", "B"], items!.Select(i => i.GetProperty("No").GetString()));
        Assert.Equal(JsonValueKind.Null, items![0].GetProperty("Empty").ValueKind);
        Assert.Contains("well-formed", ConnectionItems.Soap("<!DOCTYPE r [<!ENTITY x \"y\">]><r/>", "Item").Problem, StringComparison.Ordinal);

        var row = ConnectionItems.Row([("code", "PP-1"), ("shelf", 180), ("lot", true), ("price", 1.5m), ("none", DBNull.Value)]);
        var source = ODataMaterialRows.Read([row], Connection(new Dictionary<string, string> { ["code"] = "code", ["external_key"] = "code", ["shelf_life_days"] = "shelf", ["lot_tracked"] = "lot" }));
        Assert.Equal(("PP-1", 180, true), (source.Rows[0].Code, source.Rows[0].ShelfLifeDays!.Value!.Value, source.Rows[0].IsLotTracked!.Value));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("none").ValueKind);
    }
}
