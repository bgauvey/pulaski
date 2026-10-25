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
}
