using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Materials;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Material connections (D-068) against the real schema: the items go through the same idempotent import as a file.</summary>
[Collection(DatabaseTests.Name)]
public sealed class MaterialConnectionTests(DatabaseFixture db)
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task A_connection_imports_its_items_once_matched_on_their_key_and_records_the_run()
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var unit = await connection.ExecuteScalarAsync<string>("SELECT code FROM core.uom WHERE id = @Id", new { Id = db.UomId });
        var code = "ODATA-" + _suffix;
        var saved = await db.SendAsync(new SaveMaterialConnectionCommand(null, new SaveMaterialConnectionRequest
        {
            Code = code,
            Name = "Test ERP items",
            BaseUrl = "https://erp.example/odata",
            EntitySet = "Products",
            ExternalSystem = "ERP-" + _suffix,
            FieldMap = new Dictionary<string, string>
            {
                ["code"] = "Number",
                ["name"] = "Name",
                ["type"] = "Kind",
                ["base_unit"] = "Unit",
                ["external_key"] = "Id",
            },
        }));
        db.MaterialConnectionFetcher.Json[code] = $$"""
            [{"Id": 1, "Number": "OD-A-{{_suffix}}", "Name": "Resin", "Kind": "raw", "Unit": "{{unit}}"},
             {"Id": 2, "Number": "OD-B-{{_suffix}}", "Name": "Cap", "Kind": "packaging", "Unit": "{{unit}}"}]
            """;

        // A check saves nothing and records nothing.
        var check = await db.SendAsync(new RunMaterialConnectionCommand(saved.Id, DryRun: true, CreateClasses: false));
        Assert.Equal((false, 2), (check.Applied, check.Created));
        Assert.Null(Assert.Single(await QueryAsync<ListMaterialConnectionsQuery, IReadOnlyList<MaterialConnectionDto>>(new ListMaterialConnectionsQuery()), c => c.Id == saved.Id).LastRunAt);

        var imported = await db.SendAsync(new RunMaterialConnectionCommand(saved.Id, DryRun: false, CreateClasses: false));
        Assert.Equal((true, 2), (imported.Applied, imported.Created));
        Assert.Equal(("erp", "1"), await connection.QuerySingleAsync<(string, string)>(
            "SELECT origin, external_key FROM core.material WHERE code = @Code", new { Code = "OD-A-" + _suffix }));

        // Run again with a new name: matched on the key, updated, never duplicated.
        db.MaterialConnectionFetcher.Json[code] = db.MaterialConnectionFetcher.Json[code].Replace("\"Resin\"", "\"Resin, natural\"", StringComparison.Ordinal);
        var again = await db.SendAsync(new RunMaterialConnectionCommand(saved.Id, DryRun: false, CreateClasses: false));
        Assert.Equal((0, 1, 1), (again.Created, again.Updated, again.Unchanged));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM core.material WHERE external_system = @System AND external_key = '1'", new { System = "ERP-" + _suffix }));

        var listed = Assert.Single(await QueryAsync<ListMaterialConnectionsQuery, IReadOnlyList<MaterialConnectionDto>>(new ListMaterialConnectionsQuery()), c => c.Id == saved.Id);
        Assert.Equal("0 created, 1 updated, 1 unchanged.", listed.LastRunSummary);
        Assert.Equal("Number", listed.FieldMap["code"]);
    }
}
