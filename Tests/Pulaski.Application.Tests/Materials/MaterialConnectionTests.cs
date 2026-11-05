using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
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
        var save = new SaveMaterialConnectionHandler(f.Stores.UnitOfWork, store, f.Stores.Events, f.Events, f.Ids, f.Time);
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
            f.Stores.UnitOfWork, store, f.Stores.MaterialConnectionFetcher, new ThrowingImport(), new FixedRequestContext("ops"), f.Ids, f.Time);
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

    [Fact]
    public void A_schedule_runs_every_so_many_hours_or_at_the_next_daily_time_and_never_both()
    {
        var at = new DateTimeOffset(2026, 11, 5, 13, 30, 0, TimeSpan.Zero);
        Assert.Equal(at.AddHours(6), ConnectionSchedule.Next(new ConnectionScheduleSpec(6, []), at));
        var daily = new ConnectionScheduleSpec(null, [new TimeOnly(2, 0), new TimeOnly(14, 0)]);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero), ConnectionSchedule.Next(daily, at));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero), ConnectionSchedule.Next(daily, at.AddHours(1)));
        Assert.Null(ConnectionSchedule.Next(ConnectionScheduleSpec.None, at));

        Assert.NotNull(ConnectionSchedule.Problem(new ConnectionScheduleSpec(6, [new TimeOnly(2, 0)])));
        Assert.NotNull(ConnectionSchedule.Problem(new ConnectionScheduleSpec(0, [])));
        Assert.NotNull(ConnectionSchedule.Problem(new ConnectionScheduleSpec(null, [new(1, 0), new(2, 0), new(3, 0), new(4, 0), new(5, 0)])));
        Assert.Null(ConnectionSchedule.Problem(daily));
    }

    [Fact]
    public async Task A_due_connection_runs_once_as_the_schedule_its_import_is_recorded_and_the_next_run_planned()
    {
        var f = new HandlerFixture();
        var store = f.Stores.MaterialConnections;
        var site = Guid.CreateVersion7();
        store.Sites.Add(site);
        f.Stores.Labor.AddSite(site);
        var save = new SaveMaterialConnectionHandler(f.Stores.UnitOfWork, store, f.Stores.Events, f.Events, f.Ids, f.Time);
        var request = new SaveMaterialConnectionRequest
        {
            Code = "hr",
            Name = "HR people",
            Kind = "scim",
            BaseUrl = "https://hr.example/scim/v2",
            ExternalSystem = "HR1",
            SiteId = site,
            ScheduleEveryHours = 6,
            SwitchOffMissing = true,
        };

        // SCIM reads people, and people go to a site.
        await Assert.ThrowsAsync<ValidationException>(() => save.HandleAsync(new SaveMaterialConnectionCommand(null, request), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => save.HandleAsync(
            new SaveMaterialConnectionCommand(null, request with { SiteId = null }, ConnectionTargets.People), CancellationToken.None));
        var created = await save.HandleAsync(new SaveMaterialConnectionCommand(null, request, ConnectionTargets.People), CancellationToken.None);
        var saved = (await store.GetForUpdateAsync(created.Id, CancellationToken.None))!.Value;
        Assert.Equal(HandlerFixture.Now.AddHours(6), saved.NextRunAt);

        f.Stores.MaterialConnectionFetcher.Json["HR"] = """
            [{"schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"], "id": "u1", "displayName": "Ana Ng", "active": true,
              "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": {"employeeNumber": "1001"}}]
            """;
        var importPeople = new ImportPeopleHandler(f.Stores.UnitOfWork, f.Stores.Labor, f.Stores.Events, f.Events, f.Ids, f.Stores.Labor, new FixedRequestContext("system"), f.Time);
        var people = new RunPeopleConnectionHandler(
            f.Stores.UnitOfWork, store, f.Stores.MaterialConnectionFetcher, importPeople, new FixedRequestContext("system"), f.Ids, f.Time);
        var materials = new RunMaterialConnectionHandler(
            f.Stores.UnitOfWork, store, f.Stores.MaterialConnectionFetcher, new ThrowingImport(), new FixedRequestContext("system"), f.Ids, f.Time);
        var scheduler = new ScheduledConnections(f.Stores.UnitOfWork, store, materials, people, new FixedRequestContext("system"), f.Ids, f.Time);

        Assert.Equal(0, await scheduler.RunDueAsync(CancellationToken.None));
        f.Time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(1, await scheduler.RunDueAsync(CancellationToken.None));
        Assert.Equal(0, await scheduler.RunDueAsync(CancellationToken.None));

        var run = Assert.Single(store.Runs);
        Assert.Equal(("schedule", "applied", 1), (run.Trigger, run.Outcome, run.Created));
        Assert.Equal("HR people import (scheduled)", f.Stores.Events.Appended.Single(e => e.Type == EventTypes.PersonCreated).Reason);
        Assert.Equal(HandlerFixture.Now.AddHours(12), (await store.GetForUpdateAsync(created.Id, CancellationToken.None))!.Value.NextRunAt);

        // A source that can't be read is a failed run, visible on the list; the schedule carries on.
        f.Stores.MaterialConnectionFetcher.Problems["HR"] = "HR: the service answered 503.";
        f.Time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(1, await scheduler.RunDueAsync(CancellationToken.None));
        Assert.Equal("failed", store.Runs[^1].Outcome);
        var listed = Assert.Single(await new ListMaterialConnectionsHandler(f.Stores.UnitOfWork, store, f.Stores.MaterialConnectionFetcher)
            .HandleAsync(new ListMaterialConnectionsQuery(ConnectionTargets.People, site), CancellationToken.None));
        Assert.Equal(("failed", HandlerFixture.Now.AddHours(18)), (listed.LastRunOutcome, listed.NextRunAt));
        Assert.Empty(await new ListMaterialConnectionsHandler(f.Stores.UnitOfWork, store, f.Stores.MaterialConnectionFetcher)
            .HandleAsync(new ListMaterialConnectionsQuery(), CancellationToken.None));
    }

    [Fact]
    public void People_items_become_rows_through_the_map_with_the_connections_system()
    {
        using var document = JsonDocument.Parse("""[{"No": 1001, "Name": "Ana Ng", "Id": "W-1", "Certs": "FORK; FIRST-AID::2027-01-31"}]""");
        var connection = Connection(new Dictionary<string, string> { ["employee_number"] = "No", ["name"] = "Name", ["external_key"] = "Id", ["qualifications"] = "Certs" });
        var row = Assert.Single(ConnectionPeopleRows.Read([document.RootElement[0].Clone()], connection).Rows);
        Assert.Equal(("1001", "Ana Ng", "ERP1", "W-1"), (row.EmployeeNumber, row.DisplayName, row.ExternalSystem!, row.ExternalKey!));
        Assert.Equal([new("FORK", null, null), new("FIRST-AID", null, new DateOnly(2027, 1, 31))], row.Qualifications!);
        Assert.NotEmpty(ConnectionPeopleRows.Problems(new Dictionary<string, string> { ["employee_number"] = "No" }));
    }
}
