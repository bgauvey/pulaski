using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Labor;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Common;
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

    [Fact]
    public async Task A_scheduled_people_connection_runs_when_due_grants_qualifications_switches_off_leavers_and_keeps_its_history()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "ENT-" + _suffix, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "SITE-" + _suffix, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        await db.SendAsync(new CreateQualificationCommand(site.Id, "FORK", "Forklift", null, 12));
        var code = "HR-" + _suffix;
        var system = "HR-" + _suffix;
        var saved = await db.SendAsync(new SaveMaterialConnectionCommand(null, new SaveMaterialConnectionRequest
        {
            Code = code,
            Name = "HR people",
            Kind = "rest",
            BaseUrl = "https://hr.example/api/people",
            ExternalSystem = system,
            SiteId = site.Id,
            ScheduleDailyAt = [new TimeOnly(14, 0), new TimeOnly(2, 0)],
            SwitchOffMissing = true,
            FieldMap = new Dictionary<string, string>
            {
                ["employee_number"] = "No",
                ["name"] = "Name",
                ["external_key"] = "Id",
                ["qualifications"] = "Certs",
            },
        }, ConnectionTargets.People));

        var listed = Assert.Single(await QueryAsync<ListMaterialConnectionsQuery, IReadOnlyList<MaterialConnectionDto>>(new ListMaterialConnectionsQuery(ConnectionTargets.People, site.Id)));
        Assert.Equal([new TimeOnly(2, 0), new TimeOnly(14, 0)], listed.ScheduleDailyAt);
        Assert.NotNull(listed.NextRunAt);

        db.MaterialConnectionFetcher.Json[code] = $$"""
            [{"No": "1-{{_suffix}}", "Name": "Ana Ng", "Id": "A", "Certs": "FORK"},
             {"No": "2-{{_suffix}}", "Name": "Bo Li", "Id": "B", "Certs": ""}]
            """;
        await using var admin = await db.AdminDataSource.OpenConnectionAsync();
        async Task RunDueAsync()
        {
            await admin.ExecuteAsync("UPDATE core.material_connection SET next_run_at = now() - interval '1 minute' WHERE id = @Id", new { saved.Id });
            await using var scope = db.Services.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ScheduledConnections>().RunDueAsync(CancellationToken.None) >= 1);
        }

        await RunDueAsync();
        Assert.Equal(1, await admin.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM core.person_qualification g JOIN core.person p ON p.id = g.person_id WHERE p.external_system = @System", new { System = system }));

        // Bo has left: the next full import switches him off; Ana's grant isn't repeated.
        db.MaterialConnectionFetcher.Json[code] = $$"""[{"No": "1-{{_suffix}}", "Name": "Ana Ng", "Id": "A", "Certs": "FORK"}]""";
        await RunDueAsync();
        Assert.False(await admin.ExecuteScalarAsync<bool>("SELECT is_active FROM core.person WHERE external_system = @System AND external_key = 'B'", new { System = system }));
        Assert.Equal(1, await admin.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM core.person_qualification g JOIN core.person p ON p.id = g.person_id WHERE p.external_system = @System", new { System = system }));

        var runs = await QueryAsync<ListConnectionRunsQuery, IReadOnlyList<ConnectionRunDto>>(new ListConnectionRunsQuery(saved.Id, ConnectionTargets.People));
        Assert.Equal([("schedule", "applied"), ("schedule", "applied")], runs.Select(r => (r.Trigger, r.Outcome)));
        Assert.Equal((0, 1), (runs[0].Created, runs[0].Updated));
        Assert.True(await admin.ExecuteScalarAsync<DateTimeOffset>("SELECT next_run_at FROM core.material_connection WHERE id = @Id", new { saved.Id }) > DateTimeOffset.UtcNow);
    }
}
