using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Processes;
using Pulaski.Domain;
using Pulaski.Domain.Processes;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Process routes end to end (D-023): real SQL, as the application login, through the handlers.</summary>
[Collection(DatabaseTests.Name)]
public sealed class ProcessTests(DatabaseFixture db)
{
    private sealed record Plant(Guid Site, Guid Ext1, Guid Ext2, Guid Pack, Guid Saw);

    private async Task<Plant> CreatePlantAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(
            null, "ENT-" + suffix, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(
            enterprise.Id, "SITE-" + suffix, "Site", null, "site", "site", false, false, false, "America/Chicago"));

        async Task<Guid> LineAsync(string code) => (await db.SendAsync(new CreateEquipmentCommand(
            site.Id, $"{code}-{suffix}", code, null, "work_center", "production_line", true, true, false, null))).Id;

        var ext1 = await LineAsync("EXT-01");
        var saw = (await db.SendAsync(new CreateEquipmentCommand(
            ext1, "SAW-" + suffix, "Saw", null, "work_unit", "machine", true, false, false, null))).Id;
        return new Plant(site.Id, ext1, await LineAsync("EXT-02"), await LineAsync("PACK1"), saw);
    }

    private static ProcessDefinitionDto ExtrudeAndPack(Plant plant, Guid? packEquipment = null)
    {
        var extrude = new ProcessOperationDto
        {
            Id = Guid.CreateVersion7(),
            Number = 10,
            Name = "Extrude",
            Equipment = [new(plant.Ext1, 50m), new(plant.Ext2, 50m)],
        };
        var pack = new ProcessOperationDto { Id = Guid.CreateVersion7(), Number = 30, Name = "Pack", X = 300, Equipment = [new(packEquipment ?? plant.Pack, 100m)] };
        return new ProcessDefinitionDto
        {
            Operations = [extrude, pack],
            Dependencies = [new(Guid.CreateVersion7(), extrude.Id, pack.Id, DependencyType.AfterStart, TimeSpan.FromMinutes(15))],
        };
    }

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task A_process_is_drafted_released_and_redrafted()
    {
        var plant = await CreatePlantAsync();
        var created = await db.SendAsync(new CreateProcessCommand(plant.Site, "EXT-PACK", "Extrude and Pack", null));
        var process = await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id));
        var v1 = Assert.Single(process.Versions);
        Assert.Equal((1, ProcessVersionStatus.Draft), (v1.Number, v1.Status));

        var draft = await QueryAsync<GetProcessVersionQuery, ProcessVersionDto>(new GetProcessVersionQuery(created.Id, v1.Id));
        Assert.Equal(["no_operations"], draft.Problems.Select(p => p.Code));

        var saved = await db.SendAsync(new SaveProcessDraftCommand(created.Id, v1.Id, draft.Version, ExtrudeAndPack(plant)));
        Assert.Empty(saved.Problems);

        var reread = await QueryAsync<GetProcessVersionQuery, ProcessVersionDto>(new GetProcessVersionQuery(created.Id, v1.Id));
        Assert.Equal([10, 30], reread.Definition.Operations.Select(o => o.Number));
        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(reread.Definition.Dependencies).Lag);

        await db.SendAsync(new ReleaseProcessVersionCommand(created.Id, v1.Id, saved.Version, "First cut"));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM core.process_operation_equipment e JOIN core.process_operation o ON o.id = e.operation_id WHERE o.process_version_id = @v1",
                new { v1 = v1.Id }));
        }

        var v2 = await db.SendAsync(new StartProcessDraftCommand(created.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new StartProcessDraftCommand(created.Id)));
        await db.SendAsync(new ReleaseProcessVersionCommand(created.Id, v2.Id, v2.Version, null));

        var after = await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id));
        Assert.Equal([(2, ProcessVersionStatus.Released), (1, ProcessVersionStatus.Obsolete)], after.Versions.Select(v => (v.Number, v.Status)));
        var summary = Assert.Single(await QueryAsync<ListProcessesQuery, IReadOnlyList<ProcessSummaryDto>>(new ListProcessesQuery(plant.Site)));
        Assert.Equal((2, (int?)null), (summary.ReleasedVersion!.Value, summary.DraftVersion));
    }

    [Fact]
    public async Task A_draft_naming_a_machine_or_another_site_is_refused_and_a_released_version_is_fixed()
    {
        var plant = await CreatePlantAsync();
        var other = await CreatePlantAsync();
        var created = await db.SendAsync(new CreateProcessCommand(plant.Site, "P", "P", null));
        var v1 = (await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id))).Versions[0];
        var token = (await QueryAsync<GetProcessVersionQuery, ProcessVersionDto>(new GetProcessVersionQuery(created.Id, v1.Id))).Version;

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new SaveProcessDraftCommand(created.Id, v1.Id, token, ExtrudeAndPack(plant, plant.Saw))));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new SaveProcessDraftCommand(created.Id, v1.Id, token, ExtrudeAndPack(plant, other.Pack))));

        var saved = await db.SendAsync(new SaveProcessDraftCommand(created.Id, v1.Id, token, ExtrudeAndPack(plant)));
        await Assert.ThrowsAsync<ConflictException>(() =>
            db.SendAsync(new SaveProcessDraftCommand(created.Id, v1.Id, token, ExtrudeAndPack(plant))));
        var released = await db.SendAsync(new ReleaseProcessVersionCommand(created.Id, v1.Id, saved.Version, null));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new SaveProcessDraftCommand(created.Id, v1.Id, released.Version, ExtrudeAndPack(plant))));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new CreateProcessCommand(plant.Site, "P", "Again", null)));
    }

    [Fact]
    public async Task A_process_makes_a_class_or_single_materials_and_lists_by_material()
    {
        var plant = await CreatePlantAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var (profiles, white, black, resin) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.material_class (id, code, name) VALUES (@profiles, @classCode, 'Profiles');
                INSERT INTO core.material (id, code, name, material_type, material_class_id, base_uom_id) VALUES
                    (@white, @whiteCode, 'White', 'finished', @profiles, @uom),
                    (@black, @blackCode, 'Black', 'finished', @profiles, @uom),
                    (@resin, @resinCode, 'Resin', 'raw', NULL, @uom);
                """,
                new { profiles, white, black, resin, uom = db.UomId, classCode = "PRF-" + suffix, whiteCode = "WH-" + suffix, blackCode = "BK-" + suffix, resinCode = "RS-" + suffix });
        }

        var created = await db.SendAsync(new CreateProcessCommand(plant.Site, "SCOPE", "Scope", null));
        async Task<long> Token() => (await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id))).Version;
        async Task<IEnumerable<Guid>> For(Guid material) =>
            (await QueryAsync<ListProcessesQuery, IReadOnlyList<ProcessSummaryDto>>(new ListProcessesQuery(plant.Site, material))).Select(p => p.Id);

        await db.SendAsync(new SetProcessScopeCommand(created.Id, await Token(), false, [profiles], [resin]));
        Assert.Contains(created.Id, await For(white));
        Assert.Contains(created.Id, await For(resin));
        var process = await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id));
        Assert.Equal((false, profiles, resin), (process.MakesAllMaterials, Assert.Single(process.MakesClasses).Id, Assert.Single(process.MakesMaterials).Id));

        // Out of the scope and back in: rows are switched off and on, never deleted.
        await db.SendAsync(new SetProcessScopeCommand(created.Id, await Token(), false, [], [black]));
        Assert.DoesNotContain(created.Id, await For(white));
        Assert.Contains(created.Id, await For(black));
        await db.SendAsync(new SetProcessScopeCommand(created.Id, await Token(), false, [profiles], []));
        Assert.Contains(created.Id, await For(white));
        Assert.DoesNotContain(created.Id, await For(resin));

        var token = await Token();
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new SetProcessScopeCommand(created.Id, token, false, [Guid.CreateVersion7()], [])));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new SetProcessScopeCommand(created.Id, 1, true, [], [])));
    }

    [Fact]
    public async Task A_material_names_its_default_process()
    {
        var plant = await CreatePlantAsync();
        var created = await db.SendAsync(new CreateProcessCommand(plant.Site, "DEF", "Default", null));
        var material = Guid.CreateVersion7();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO core.material (id, code, name, material_type, base_uom_id) VALUES (@material, @code, 'M', 'finished', @uom)",
                new { material, code = "M-" + Guid.NewGuid().ToString("N")[..6], uom = db.UomId });
        }

        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new SetDefaultProcessCommand(material, created.Id)));
        var process = await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id));
        await db.SendAsync(new SetProcessScopeCommand(created.Id, process.Version, true, [], []));
        await db.SendAsync(new SetDefaultProcessCommand(material, created.Id));

        Assert.Equal(material, Assert.Single((await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id))).DefaultFor).Id);
        await db.SendAsync(new SetDefaultProcessCommand(material, null));
        Assert.Empty((await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id))).DefaultFor);
        await Assert.ThrowsAsync<NotFoundException>(() => db.SendAsync(new SetDefaultProcessCommand(Guid.CreateVersion7(), null)));
    }
}
