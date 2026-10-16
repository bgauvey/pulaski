using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.WorkOrders;
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

    [Fact]
    public async Task A_work_order_is_made_from_the_released_process_with_its_jobs_and_notes()
    {
        var plant = await CreatePlantAsync();
        var processId = await ReleasedExtrudeAndPackAsync(plant);
        foreach (var line in new[] { plant.Ext1, plant.Ext2, plant.Pack })
        {
            await db.ExecuteAsync(
                $"""
                INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
                VALUES ('{line}', '{db.MaterialId}', 1000, '{db.UomId}', now() - interval '1 day')
                """);
        }

        var start = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(-5));
        var number = "WO-" + Guid.NewGuid().ToString("N")[..8];
        var created = await db.SendAsync(new CreateWorkOrderCommand(
            processId, number, db.MaterialId, 1500.5m, db.UomId, start, DueAt: null, Priority: 2, Notes: "  Rush for the dealer show.  "));

        var reads = db.Services.GetRequiredService<IWorkOrderReadStore>();
        var row = (await reads.GetAsync(created.Id, CancellationToken.None))!;
        Assert.Equal(start, row.PlannedStart);
        Assert.Equal(TimeSpan.Zero, row.PlannedStart!.Value.Offset);
        Assert.Equal(1500.5m, row.PlannedQty);
        Assert.Equal(("EXT-PACK", 1), (row.ProcessCode, row.ProcessVersionNumber));
        Assert.Equal("Rush for the dealer show.", row.Notes);
        Assert.Equal(created.Version, row.Version);

        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            var jobs = (await connection.QueryAsync<(string JobNumber, Guid EquipmentId, decimal PlannedQty, DateTimeOffset EarliestStart, int Operation)>(
                """
                SELECT j.job_number, j.equipment_id, j.planned_qty, j.earliest_start, o.operation_number
                FROM core.job j JOIN core.process_operation o ON o.id = j.process_operation_id
                WHERE j.work_order_id = @Id ORDER BY j.job_number
                """,
                new { created.Id })).ToList();
            Assert.Equal(
                [($"{number}-10-1", plant.Ext1, 750.25m, start, 10), ($"{number}-10-2", plant.Ext2, 750.25m, start, 10),
                 ($"{number}-30", plant.Pack, 1500.5m, start.AddMinutes(15), 30)],
                jobs);

            var events = await connection.QueryAsync<string>(
                "SELECT type FROM evt.event WHERE subject = @Id OR subject IN (SELECT id FROM core.job WHERE work_order_id = @Id) ORDER BY position",
                new { created.Id });
            Assert.Equal([EventTypes.WorkOrderCreated, EventTypes.JobCreated, EventTypes.JobCreated, EventTypes.JobCreated], events);
        }

        // The order number is unique per site; the whole order, jobs included, is refused.
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new CreateWorkOrderCommand(
            processId, number, db.MaterialId, 1, db.UomId, start, null, 0, null)));
    }

    [Fact]
    public async Task A_work_order_needs_a_capability_on_every_line_of_its_process_and_the_check_says_which()
    {
        var plant = await CreatePlantAsync();
        var processId = await ReleasedExtrudeAndPackAsync(plant);
        await db.ExecuteAsync(
            $"""
            INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
            VALUES ('{plant.Ext1}', '{db.MaterialId}', 1000, '{db.UomId}', now() - interval '1 day'),
                   ('{plant.Ext2}', '{db.MaterialId}', 1000, '{db.UomId}', now() - interval '1 day')
            """);

        // The form's check and creating the order agree: only PACK1 lacks a capability.
        var check = await QueryAsync<CheckProcessForOrderQuery, ProcessOrderCheckDto>(new CheckProcessForOrderQuery(processId, db.MaterialId));
        var problem = Assert.Single(check.Problems);
        Assert.Equal(("no_capability", 30), (problem.Code, problem.OperationNumber));

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreateWorkOrderCommand(
            processId, "WO-X", db.MaterialId, 10, db.UomId, DateTimeOffset.UtcNow, null, 0, null)));
        Assert.Equal(problem.Message, refused.Message);
        Assert.StartsWith("Operation 30 (Pack): PACK1-", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Extrude and Pack, making every material, released as version 1.</summary>
    private async Task<Guid> ReleasedExtrudeAndPackAsync(Plant plant)
    {
        var created = await db.SendAsync(new CreateProcessCommand(plant.Site, "EXT-PACK", "Extrude and Pack", null));
        var process = await QueryAsync<GetProcessQuery, ProcessDto>(new GetProcessQuery(created.Id));
        await db.SendAsync(new SetProcessScopeCommand(created.Id, process.Version, true, [], []));
        var v1 = Assert.Single(process.Versions);
        var draft = await QueryAsync<GetProcessVersionQuery, ProcessVersionDto>(new GetProcessVersionQuery(created.Id, v1.Id));
        var saved = await db.SendAsync(new SaveProcessDraftCommand(created.Id, v1.Id, draft.Version, ExtrudeAndPack(plant)));
        await db.SendAsync(new ReleaseProcessVersionCommand(created.Id, v1.Id, saved.Version, null));
        return created.Id;
    }
}
