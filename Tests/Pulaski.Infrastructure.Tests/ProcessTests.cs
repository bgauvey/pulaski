using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Application.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Boms;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Processes;
using Pulaski.Domain.Scheduling;

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

        var shopOrder = (await reads.GetShopOrderJobsAsync(created.Id, CancellationToken.None))!;
        Assert.Equal("America/Chicago", shopOrder.SiteTimeZone);
        Assert.Equal(
            [(10, $"{number}-10-1"), (10, $"{number}-10-2"), (30, $"{number}-30")],
            shopOrder.Jobs.Select(job => (job.OperationNumber, job.JobNumber)));
        Assert.All(shopOrder.Jobs, job => Assert.Equal("m", job.UomCode));

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

    [Fact]
    public async Task A_released_bom_plans_each_job_components_when_the_order_is_made()
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

        var (blend, endCap) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await db.ExecuteAsync(
            $"""
            INSERT INTO core.material (id, code, name, material_type, base_uom_id)
            VALUES ('{blend}', 'BLEND-{blend.ToString("N")[^8..]}', 'Blend', 'intermediate', '{db.UomId}'),
                   ('{endCap}', 'CAP-{endCap.ToString("N")[^8..]}', 'End cap', 'packaging', '{db.UomId}')
            """);

        var bom = await db.SendAsync(new CreateBomCommand(db.MaterialId, processId));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new CreateBomCommand(db.MaterialId, processId)));
        var header = await QueryAsync<GetBomQuery, BomDto>(new GetBomQuery(bom.Id));
        var draft = await QueryAsync<GetBomVersionQuery, BomVersionDto>(new GetBomVersionQuery(bom.Id, header.Versions.Single().Id));
        Assert.Equal(["no_items"], draft.Problems.Select(p => p.Code));

        var saved = await db.SendAsync(new SaveBomDraftCommand(bom.Id, draft.Id, draft.Version, new BomDefinitionDto
        {
            BaseQty = 1000m,
            BaseUomId = db.UomId,
            Items =
            [
                new BomItemDto { Id = Guid.CreateVersion7(), LineNo = 10, ComponentMaterialId = blend, QtyPer = 1450m, UomId = db.UomId, ScrapFactor = 0.02m, ConsumptionMethod = ConsumptionMethod.TimeWindow, OperationNumber = 10 },
                new BomItemDto { Id = Guid.CreateVersion7(), LineNo = 20, ComponentMaterialId = endCap, QtyPer = 84m, UomId = db.UomId, ConsumptionMethod = ConsumptionMethod.Backflush, OperationNumber = 30 },
            ],
        }));
        Assert.Empty(saved.Problems);
        await db.SendAsync(new ReleaseBomVersionCommand(bom.Id, draft.Id, saved.Version, null));

        var reread = await QueryAsync<GetBomVersionQuery, BomVersionDto>(new GetBomVersionQuery(bom.Id, draft.Id));
        Assert.Equal((BomVersionStatus.Released, 1000m, 2), (reread.Status, reread.Definition.BaseQty, reread.Definition.Items.Count));
        Assert.Equal(1, Assert.Single(await QueryAsync<ListBomsQuery, IReadOnlyList<BomSummaryDto>>(new ListBomsQuery(db.MaterialId)), b => b.Id == bom.Id).ReleasedVersion);

        var order = await db.SendAsync(new CreateWorkOrderCommand(
            processId, "WO-" + Guid.NewGuid().ToString("N")[..8], db.MaterialId, 2000m, db.UomId, DateTimeOffset.UtcNow, null, 0, null));

        var reads = db.Services.GetRequiredService<IWorkOrderReadStore>();
        Assert.Equal(1, (await reads.GetAsync(order.Id, CancellationToken.None))!.BomVersionNumber);
        var components = await QueryAsync<ListPlannedComponentsQuery, IReadOnlyList<PlannedComponentDto>>(new ListPlannedComponentsQuery(order.Id));
        Assert.Equal(
            [(10, blend, 1479m), (10, blend, 1479m), (30, endCap, 168m)],
            components.Select(c => (c.OperationNumber!.Value, c.MaterialId, c.PlannedQty)));
    }

    [Fact]
    public async Task Jobs_added_later_get_components_and_a_process_change_that_breaks_a_bom_is_reported()
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

        var endCap = Guid.CreateVersion7();
        await db.ExecuteAsync(
            $"""
            INSERT INTO core.material (id, code, name, material_type, base_uom_id)
            VALUES ('{endCap}', 'CAP-{endCap.ToString("N")[^8..]}', 'End cap', 'packaging', '{db.UomId}')
            """);
        var bom = await db.SendAsync(new CreateBomCommand(db.MaterialId, processId));
        var bomDraft = await QueryAsync<GetBomVersionQuery, BomVersionDto>(new GetBomVersionQuery(
            bom.Id, (await QueryAsync<GetBomQuery, BomDto>(new GetBomQuery(bom.Id))).Versions.Single().Id));
        var savedBom = await db.SendAsync(new SaveBomDraftCommand(bom.Id, bomDraft.Id, bomDraft.Version, new BomDefinitionDto
        {
            BaseQty = 1000m,
            BaseUomId = db.UomId,
            Items = [new BomItemDto { Id = Guid.CreateVersion7(), LineNo = 10, ComponentMaterialId = endCap, QtyPer = 84m, UomId = db.UomId, ConsumptionMethod = ConsumptionMethod.Backflush, OperationNumber = 30 }],
        }));
        await db.SendAsync(new ReleaseBomVersionCommand(bom.Id, bomDraft.Id, savedBom.Version, null));

        var order = await db.SendAsync(new CreateWorkOrderCommand(
            processId, "WO-" + Guid.NewGuid().ToString("N")[..8], db.MaterialId, 2000m, db.UomId, DateTimeOffset.UtcNow, null, 0, null));

        // Extrude and Pack each make the 2000 m: on lines is 2000, not the 4000 the jobs add up to.
        var progress = await QueryAsync<ListWorkOrderProgressQuery, IReadOnlyList<WorkOrderProgressDto>>(new ListWorkOrderProgressQuery(plant.Site));
        Assert.Equal(2000m, Assert.Single(progress, p => p.Order.Id == order.Id).PlacedQty);

        // A job added from the schedule on PACK1 is a Pack job, with its end caps.
        var added = await db.SendAsync(new CreateJobCommand(
            plant.Pack, "J-" + Guid.NewGuid().ToString("N")[..8], JobType.Production, order.Id, 500m, null, false, null, null));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            var (operation, quantity) = await connection.QuerySingleAsync<(int, decimal)>(
                """
                SELECT o.operation_number, jm.planned_qty
                FROM core.job j
                JOIN core.process_operation o ON o.id = j.process_operation_id
                JOIN core.job_material jm ON jm.job_id = j.id
                WHERE j.id = @Id
                """,
                new { added.Id });
            Assert.Equal((30, 42m), (operation, quantity));
        }

        // Version 2 renumbers Pack to 40: the release goes through but reports the BOM it broke.
        var v2 = await db.SendAsync(new StartProcessDraftCommand(processId));
        var draft = await QueryAsync<GetProcessVersionQuery, ProcessVersionDto>(new GetProcessVersionQuery(processId, v2.Id));
        var renumbered = draft.Definition with
        {
            Operations = [.. draft.Definition.Operations.Select(o => o.Number == 30 ? o with { Number = 40 } : o)],
        };
        var saved = await db.SendAsync(new SaveProcessDraftCommand(processId, v2.Id, draft.Version, renumbered));
        var released = await db.SendAsync(new ReleaseProcessVersionCommand(processId, v2.Id, saved.Version, null));

        Assert.Contains("operation 30", Assert.Single(released.Warnings), StringComparison.Ordinal);
        var broken = await QueryAsync<GetBomQuery, BomDto>(new GetBomQuery(bom.Id));
        Assert.Equal(30, Assert.Single(broken.MissingOperations));
        var check = await QueryAsync<CheckProcessForOrderQuery, ProcessOrderCheckDto>(new CheckProcessForOrderQuery(processId, db.MaterialId));
        Assert.Contains(check.Problems, p => p.Code == "bom_operation");
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreateWorkOrderCommand(
            processId, "WO-" + Guid.NewGuid().ToString("N")[..8], db.MaterialId, 10m, db.UomId, DateTimeOffset.UtcNow, null, 0, null)));
        Assert.Contains("names operation 30", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Erp_orders_are_made_once_follow_the_erp_while_planned_wait_for_a_planner_after_and_take_batches_by_rule()
    {
        var plant = await CreatePlantAsync();
        await ReleasedExtrudeAndPackAsync(plant);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        CreatedResponse material = await db.SendAsync(new Application.Materials.CreateMaterialCommand("ERPM-" + suffix, "Blend", null, "intermediate", null, db.UomId, true, null));
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var materialVersion = await connection.ExecuteScalarAsync<long>("SELECT xmin::text::bigint FROM core.material WHERE id = @Id", new { material.Id });
        await db.SendAsync(new Application.Materials.SetBatchingCommand(material.Id, materialVersion, 50m, 2m, "near"));
        foreach (var line in new[] { plant.Ext1, plant.Ext2, plant.Pack })
        {
            await db.ExecuteAsync(
                $"""
                INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
                VALUES ('{line}', '{material.Id}', 1000, '{db.UomId}', now() - interval '1 day')
                """);
        }

        var siteCode = await connection.ExecuteScalarAsync<string>("SELECT code FROM core.equipment WHERE id = @Id", new { Id = plant.Site });
        var uomCode = await connection.ExecuteScalarAsync<string>("SELECT code FROM core.uom WHERE id = @Id", new { Id = db.UomId });
        var start = DateTimeOffset.UtcNow.AddDays(2);
        ErpWorkOrderRequest Order(string key, decimal qty, string? version = null, bool cancelled = false) => new()
        {
            ExternalSystem = "SAP-" + suffix,
            ExternalKey = key,
            ExternalVersion = version,
            SiteCode = siteCode!,
            OrderNumber = $"{key}-{suffix}",
            MaterialCode = "ERPM-" + suffix,
            Quantity = qty,
            UomCode = uomCode!,
            PlannedStart = start,
            Cancelled = cancelled,
        };
        async Task<ErpOrderResultDto> ImportAsync(ErpWorkOrderRequest order) =>
            Assert.Single((await db.SendAsync(new ImportWorkOrdersCommand([order], null))).Results);
        var reads = db.Services.GetRequiredService<IWorkOrderReadStore>();
        async Task<IReadOnlyList<decimal>> JobQuantitiesAsync(Guid id) =>
            [.. await connection.QueryAsync<decimal>("SELECT planned_qty FROM core.job WHERE work_order_id = @Id AND status <> 'cancelled' ORDER BY job_number", new { Id = id })];

        // Made once, from the only process that makes it, with jobs; the same version again changes nothing.
        var a = await ImportAsync(Order("A", 1000m, "1"));
        Assert.Equal(ErpOrderAction.Created, a.Action);
        Assert.Equal([500m, 500m, 1000m], await JobQuantitiesAsync(a.WorkOrderId!.Value));
        Assert.Equal(ErpOrderAction.Unchanged, (await ImportAsync(Order("A", 1000m, "1"))).Action);
        var row = (await reads.GetAsync(a.WorkOrderId.Value, CancellationToken.None))!;
        Assert.Equal(("erp", "SAP-" + suffix, "A", "1"), (row.Origin, row.ExternalSystem!, row.ExternalKey!, row.ExternalVersion!));

        // While planned, the ERP's change applies and the jobs are re-planned.
        Assert.Equal(ErpOrderAction.Updated, (await ImportAsync(Order("A", 1100m, "2"))).Action);
        Assert.Equal([550m, 550m, 1100m], await JobQuantitiesAsync(a.WorkOrderId.Value));

        // Not whole batches: refused by default; rounded up once the site says so; held where the material says so.
        var refused = await ImportAsync(Order("B", 1025m));
        Assert.Equal(ErpOrderAction.Refused, refused.Action);
        Assert.Contains("Send 1,000 or 1,050", refused.Messages[0], StringComparison.Ordinal);
        await db.SendAsync(new SetErpBatchRuleCommand(plant.Site, "round_up"));
        var b = await ImportAsync(Order("B", 1025m));
        row = (await reads.GetAsync(b.WorkOrderId!.Value, CancellationToken.None))!;
        Assert.Equal((1050m, 1025m, "rounded_up"), (row.PlannedQty, row.ErpQty!.Value, row.BatchAdjustment!));

        materialVersion = await connection.ExecuteScalarAsync<long>("SELECT xmin::text::bigint FROM core.material WHERE id = @Id", new { material.Id });
        await db.SendAsync(new SetMaterialErpBatchRuleCommand(material.Id, materialVersion, "hold"));
        var c = await ImportAsync(Order("C", 1025m));
        row = (await reads.GetAsync(c.WorkOrderId!.Value, CancellationToken.None))!;
        Assert.True(row.QuantityHeld);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new DispatchWorkOrderCommand(row.Id, row.Version)));
        await db.SendAsync(new SetWorkOrderQuantityCommand(row.Id, row.Version, 1000m));
        row = (await reads.GetAsync(c.WorkOrderId.Value, CancellationToken.None))!;
        Assert.Equal((false, 1000m, "rounded_down"), (row.QuantityHeld, row.PlannedQty, row.BatchAdjustment!));

        // Once dispatched, a change waits for a planner: accepted, it re-plans; rejected, it is dropped with a reason.
        row = (await reads.GetAsync(a.WorkOrderId.Value, CancellationToken.None))!;
        await db.SendAsync(new DispatchWorkOrderCommand(row.Id, row.Version));
        Assert.Equal(ErpOrderAction.Pending, (await ImportAsync(Order("A", 1200m, "3"))).Action);
        row = (await reads.GetAsync(a.WorkOrderId.Value, CancellationToken.None))!;
        Assert.Equal((1100m, 1200m), (row.PlannedQty, row.PendingChange!.Quantity));
        await db.SendAsync(new AcceptErpChangeCommand(row.Id, row.Version));
        Assert.Equal([600m, 600m, 1200m], await JobQuantitiesAsync(a.WorkOrderId.Value));
        Assert.Equal(ErpOrderAction.Pending, (await ImportAsync(Order("A", 1300m, "4"))).Action);
        row = (await reads.GetAsync(a.WorkOrderId.Value, CancellationToken.None))!;
        await db.SendAsync(new RejectErpChangeCommand(row.Id, row.Version, "Capacity is booked"));
        row = (await reads.GetAsync(a.WorkOrderId.Value, CancellationToken.None))!;
        Assert.Equal((1200m, (ErpPendingChangeDto?)null), (row.PlannedQty, row.PendingChange));

        // Cancelled in the ERP while planned: cancelled here, with its jobs.
        Assert.Equal(ErpOrderAction.Cancelled, (await ImportAsync(Order("B", 1025m, cancelled: true))).Action);
        Assert.Equal("cancelled", (await reads.GetAsync(b.WorkOrderId.Value, CancellationToken.None))!.Status);
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
