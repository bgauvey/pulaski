using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Materials;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Materials;

/// <summary>Importing a file through a material source adapter (D-053): the adapter reads, the bulk import decides.</summary>
public class ImportMaterialFileHandlerTests
{
    private static string Item(string name) => $"""
        <MaterialInformation xmlns="http://www.mesa.org/xml/B2MML">
          <MaterialDefinition>
            <ID>4711</ID>
            <Description>{name}</Description>
            <MaterialDefinitionProperty><ID>MaterialType</ID><Value><ValueString>raw</ValueString></Value></MaterialDefinitionProperty>
            <MaterialDefinitionProperty><ID>BaseUnit</ID><Value><ValueString>kg</ValueString></Value></MaterialDefinitionProperty>
          </MaterialDefinition>
        </MaterialInformation>
        """;

    private readonly HandlerFixture _f = new();
    private readonly IMaterialSourceAdapter[] _adapters = [new CsvMaterialSource(), new B2mmlMaterialSource()];

    public ImportMaterialFileHandlerTests() => Admin.AddUom("kg");

    private InMemoryMaterialAdmin Admin => _f.Stores.MaterialAdmin;

    private Task<ImportMaterialsResponse> RunAsync(string source, string text, string? externalSystem = null, bool dryRun = false) =>
        new ImportMaterialFileHandler(_adapters, new ImportMaterialsHandler(_f.Stores.UnitOfWork, Admin, _f.Stores.Events, _f.Events, _f.Ids))
            .HandleAsync(new ImportMaterialFileCommand(source, text, externalSystem, dryRun), CancellationToken.None);

    private Material ByCode(string code) => Admin.FindByCodeForUpdateAsync(code, CancellationToken.None).Result!.Value;

    [Fact]
    public async Task A_b2mml_item_from_a_named_system_is_created_then_matched_on_its_key()
    {
        var first = await RunAsync("b2mml", Item("Resin"), "ERP1");
        Assert.Equal((true, 1), (first.Applied, first.Created));
        var created = ByCode("4711");
        Assert.Equal((MaterialOrigin.Erp, "ERP1", "4711"), (created.Origin, created.ExternalSystem, created.ExternalKey));
        Assert.Equal("B2MML import", _f.Stores.Events.Appended[^1].Reason);

        var renamed = await RunAsync("B2MML", Item("Resin, natural"), "ERP1");
        Assert.Equal(["name"], Assert.Single(renamed.Rows).Changes);
        Assert.Equal(created.Id, ByCode("4711").Id);

        var again = await RunAsync("b2mml", Item("Resin, natural"), "ERP1");
        Assert.Equal("unchanged", Assert.Single(again.Rows).Action);

        // Without the system the same ID is a Pulaski code, and that code belongs to the ERP's item.
        var local = await RunAsync("b2mml", Item("Resin, natural"));
        Assert.Contains("comes from ERP1", Assert.Single(local.Rows).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_goes_through_the_same_command_and_keeps_its_reason()
    {
        var result = await RunAsync("csv", "code,name,type,base_unit\nA,A,raw,kg\n", dryRun: true);

        Assert.Equal((false, 1, 0), (result.Applied, result.Created, _f.Stores.UnitOfWork.Committed));
        await RunAsync("csv", "code,name,type,base_unit\nA,A,raw,kg\n");
        Assert.Equal("CSV import", _f.Stores.Events.Appended[^1].Reason);
    }

    [Fact]
    public async Task An_unknown_source_is_not_found_and_csv_takes_no_external_system()
    {
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => RunAsync("idoc", "x"));
        Assert.Contains("csv, b2mml", missing.Message, StringComparison.Ordinal);

        var invalid = await Assert.ThrowsAsync<ValidationException>(() => RunAsync("csv", "code,name,type,base_unit\nA,A,raw,kg\n", "ERP1"));
        Assert.Contains("externalSystem", invalid.Errors.Keys);
    }

    [Fact]
    public async Task The_sources_are_listed_with_what_a_file_picker_needs()
    {
        var sources = await new ListMaterialSourcesHandler(_adapters).HandleAsync(new ListMaterialSourcesQuery(), CancellationToken.None);

        Assert.Equal(["csv", "b2mml"], sources.Select(s => s.Name));
        Assert.Equal((false, true), (sources[0].TakesExternalSystem, sources[1].TakesExternalSystem));
        Assert.Contains(".xml", sources[1].FileTypes);
    }
}
