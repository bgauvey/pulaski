using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Execution;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Execution;
using Pulaski.Domain;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Job execution and production reporting end to end (D-035): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class ExecutionTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private sealed record Plant(Guid Site, Guid Extruder, Guid Packer, Guid Staging, Guid Order, Guid Extrude, Guid Pack, Guid Scrap);

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(string sql, object args)
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private Task<long> VersionAsync(Guid jobId) => ScalarAsync<long>("SELECT xmin::text::bigint FROM core.job WHERE id = @Id", new { Id = jobId });

    /// <summary>
    /// A dispatched order made by a two-operation process: Pack (20) starts after Extrude (10) ends.
    /// Extruder runs it at an ideal 100 m/h; the packer has a capability too.
    /// </summary>
    private async Task<Plant> PlantAsync()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "ENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "SITE-" + _s, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        CreatedResponse extruder = await db.SendAsync(new CreateEquipmentCommand(site.Id, "EXT-" + _s, "Extruder", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse packer = await db.SendAsync(new CreateEquipmentCommand(site.Id, "PCK-" + _s, "Packer", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse staging = await db.SendAsync(new CreateEquipmentCommand(site.Id, "STG-" + _s, "Staging", null, "work_center", "storage_zone", false, false, true, null));

        Guid process = Guid.CreateVersion7(), version = Guid.CreateVersion7(), op10 = Guid.CreateVersion7(), op20 = Guid.CreateVersion7();
        Guid order = Guid.CreateVersion7(), extrude = Guid.CreateVersion7(), pack = Guid.CreateVersion7(), scrap = Guid.CreateVersion7();
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO core.process (id, site_id, code, name, makes_all_materials) VALUES (@process, @site, 'P-' || @s, 'Extrude and pack', true);
            INSERT INTO core.process_version (id, process_id, version_number, status, released_at) VALUES (@version, @process, 1, 'released', now());
            INSERT INTO core.process_operation (id, process_version_id, operation_number, name) VALUES (@op10, @version, 10, 'Extrude'), (@op20, @version, 20, 'Pack');
            INSERT INTO core.process_dependency (id, from_operation_id, to_operation_id, dependency_type) VALUES (gen_random_uuid(), @op10, @op20, 'after_end');

            INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
            VALUES (@extruder, @material, 100, @uom, now() - interval '1 day'), (@packer, @material, 500, @uom, now() - interval '1 day');

            INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, status, process_version_id)
            VALUES (@order, @site, 'WO-' || @s, @material, 1000, @uom, 'dispatched', @version);
            INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id, status, process_operation_id, dispatched_at)
            VALUES (@extrude, @site, @order, 'J10-' || @s, 'production', @extruder, @material, 1000, @uom, 'dispatched', @op10, now()),
                   (@pack,    @site, @order, 'J20-' || @s, 'production', @packer,   @material, 1000, @uom, 'dispatched', @op20, now());

            INSERT INTO core.reason_code (id, site_id, reason_kind, code, name) VALUES (@scrap, @site, 'scrap', 'SCR-' || @s, 'Start-up scrap');
            """,
            new
            {
                s = _s,
                site = site.Id,
                extruder = extruder.Id,
                packer = packer.Id,
                material = db.MaterialId,
                uom = db.UomId,
                process,
                version,
                op10,
                op20,
                order,
                extrude,
                pack,
                scrap,
            });
        return new Plant(site.Id, extruder.Id, packer.Id, staging.Id, order, extrude, pack, scrap);
    }

    [Fact]
    public async Task A_job_runs_reports_within_its_rate_and_hands_on_to_the_next_operation()
    {
        var plant = await PlantAsync();

        // Pack waits for Extrude to end.
        var early = await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new StartJobCommand(plant.Pack, await VersionAsync(plant.Pack))));
        Assert.Contains("Waiting for operation 10 Extrude to end", early.Message, StringComparison.Ordinal);

        var started = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        Assert.Equal("in_progress", await ScalarAsync<string>("SELECT status FROM core.work_order WHERE id = @Id", new { Id = plant.Order }));
        Assert.Equal("firmed", await ScalarAsync<string>("SELECT status FROM core.job WHERE id = @Id", new { Id = plant.Pack }));

        // The run started a moment ago, so a large report is more than the line can make.
        var tooMuch = await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new ReportProductionCommand(plant.Extrude, null, 1000m, 0m, null, null, plant.Staging)));
        Assert.Contains("is more than", tooMuch.Message, StringComparison.Ordinal);

        // Back-date the run an hour so there is room: 100 m/h x 1 h x 110% = 110 m.
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE core.job_run SET started_at = started_at - interval '1 hour' WHERE id = @Id", new { Id = started.RunId });
        }

        var report = await db.SendAsync(new ReportProductionCommand(plant.Extrude, null, 95m, 5m, null, plant.Scrap, plant.Staging));
        Assert.Equal((95m, 5m), (report.RunGoodQty, report.RunScrapQty));
        Assert.NotNull(report.Lot);

        var board = await QueryAsync<GetLineBoardQuery, IReadOnlyList<LineBoardRowDto>>(new GetLineBoardQuery(plant.Site));
        var extruderRow = Assert.Single(board, r => r.LineId == plant.Extruder);
        Assert.Equal((95m, 5m), (extruderRow.Running!.RunGoodQty, extruderRow.Running.RunScrapQty));
        Assert.True(extruderRow.Running.CapQty >= 110m);
        var packRow = Assert.Single(Assert.Single(board, r => r.LineId == plant.Packer).Next);
        Assert.False(packRow.IsReady);

        // Complete Extrude, then add what was found afterwards into its ended run.
        var done = await db.SendAsync(new CompleteJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        Assert.Contains(done.Notes, n => n.Contains("can start now", StringComparison.Ordinal));
        var late = await db.SendAsync(new ReportProductionCommand(plant.Extrude, started.RunId, 9m, 0m, null, null, plant.Staging));
        Assert.Equal(104m, late.RunGoodQty);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new ReportProductionCommand(plant.Extrude, started.RunId, 50m, 0m, null, null, plant.Staging)));

        var job = await QueryAsync<GetJobExecutionQuery, JobExecutionDto>(new GetJobExecutionQuery(plant.Extrude));
        Assert.Equal(("completed", 104m, 5m), (job.Status, job.GoodQty, job.ScrapQty));
        Assert.Equal(2, job.Reports.Count);
        Assert.True(job.Reports[0].IsLate);
        Assert.Equal("Start-up scrap", job.Reports[1].ScrapReason);
        Assert.Equal(2, await ScalarAsync<int>(
            "SELECT count(*)::int FROM ts.lot_event WHERE job_id = @Id AND run_id = @Run AND event_kind = 'produced'", new { Id = plant.Extrude, Run = started.RunId }));

        // Pack can start now; completing it completes the order, and closing the order closes its jobs.
        await db.SendAsync(new StartJobCommand(plant.Pack, await VersionAsync(plant.Pack), SkipChangeover: true));
        await db.SendAsync(new CompleteJobCommand(plant.Pack, await VersionAsync(plant.Pack)));
        Assert.Equal("completed", await ScalarAsync<string>("SELECT status FROM core.work_order WHERE id = @Id", new { Id = plant.Order }));

        var orderVersion = await ScalarAsync<long>("SELECT xmin::text::bigint FROM core.work_order WHERE id = @Id", new { Id = plant.Order });
        await db.SendAsync(new ChangeWorkOrderStatusCommand(plant.Order, orderVersion, WorkOrderStatus.Closed, null));
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*)::int FROM core.job WHERE work_order_id = @Id AND status = 'closed'", new { Id = plant.Order }));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new ReportProductionCommand(plant.Extrude, started.RunId, 1m, 0m, null, null, plant.Staging)));
    }

    [Fact]
    public async Task A_paused_job_frees_the_line_and_the_site_policy_sets_the_tolerance()
    {
        var plant = await PlantAsync();
        var policy = await QueryAsync<GetExecutionPolicyQuery, ExecutionPolicyDto>(new GetExecutionPolicyQuery(plant.Site));
        Assert.Equal((true, 110m), (policy.IsDefault, policy.RateTolerancePct));
        await db.SendAsync(new SetExecutionPolicyCommand(plant.Site, 150m, 120m));
        Assert.Equal(150m, (await QueryAsync<GetExecutionPolicyQuery, ExecutionPolicyDto>(new GetExecutionPolicyQuery(plant.Site))).RateTolerancePct);

        var started = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        await db.SendAsync(new PauseJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        Assert.NotNull(await ScalarAsync<DateTimeOffset?>("SELECT ended_at FROM core.job_run WHERE id = @Id", new { Id = started.RunId }));
        Assert.Equal("paused", await ScalarAsync<string>("SELECT status FROM core.job WHERE id = @Id", new { Id = plant.Extrude }));

        var resumed = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        Assert.Equal(2, await ScalarAsync<int>("SELECT run_no FROM core.job_run WHERE id = @Id", new { Id = resumed.RunId }));
    }
}
