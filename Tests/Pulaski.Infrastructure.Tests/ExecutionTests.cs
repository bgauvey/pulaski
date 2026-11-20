using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Execution;
using Pulaski.Application.Materials;
using Pulaski.Application.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Scheduling;
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
    public async Task The_schedule_holds_pack_until_extrude_is_projected_to_end()
    {
        // D-046: Pack (20) starts after Extrude (10) ends. Extruding 1000 m at 100 m/h takes 10 h; packing at 500 m/h, 2 h.
        var plant = await PlantAsync();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.run_standard (equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at)
                VALUES (@extruder, @material, @uom, 100, 'ideal_rate', now()), (@packer, @material, @uom, 500, 'ideal_rate', now());
                """,
                new { extruder = plant.Extruder, packer = plant.Packer, material = db.MaterialId, uom = db.UomId });
        }

        var extrude = Assert.Single((await QueryAsync<GetLineTimelineQuery, LineTimelineDto>(new GetLineTimelineQuery(plant.Extruder))).Segments);
        var pack = Assert.Single((await QueryAsync<GetLineTimelineQuery, LineTimelineDto>(new GetLineTimelineQuery(plant.Packer))).Segments);

        // Each timeline is read at its own "now", a moment apart.
        Assert.Equal(TimeSpan.FromHours(10), extrude.End - extrude.Start);
        Assert.Equal(pack.LinkReadyAt, pack.Start);
        Assert.InRange(pack.Start - extrude.End, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromHours(2), pack.End - pack.Start);

        // Dispatching publishes the same: Pack's baseline starts when Extrude's ends.
        var orderVersion = () => ScalarAsync<long>("SELECT xmin::text::bigint FROM core.work_order WHERE id = @Id", new { Id = plant.Order });
        await db.SendAsync(new UndispatchWorkOrderCommand(plant.Order, await orderVersion()));
        await db.SendAsync(new DispatchWorkOrderCommand(plant.Order, await orderVersion()));
        var extrudeEnd = await ScalarAsync<DateTimeOffset>("SELECT planned_end FROM core.job WHERE id = @Id", new { Id = plant.Extrude });
        Assert.Equal(extrudeEnd, await ScalarAsync<DateTimeOffset>("SELECT planned_start FROM core.job WHERE id = @Id", new { Id = plant.Pack }));
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

        // By shift (D-044): with no shifts the line's reports fall on the site's day, and add up to the job's runs.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(TestTime.Now, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")).DateTime);
        var byShift = await QueryAsync<GetProductionByShiftQuery, ProductionByShiftDto>(new GetProductionByShiftQuery(plant.Site, today.AddDays(-1), today.AddDays(1)));
        var rows = byShift.Rows.Where(r => r.JobId == plant.Extrude).ToList();
        Assert.Equal((104m, 5m, 2), (rows.Sum(r => r.GoodQty), rows.Sum(r => r.ScrapQty), rows.Sum(r => r.Reports)));
        Assert.All(rows, r => Assert.Null(r.ShiftCode));
        var check = Assert.Single(byShift.Jobs, j => j.JobId == plant.Extrude);
        Assert.Equal((true, 104m, 5m), (check.MatchesRuns, check.RunGoodQty, check.RunScrapQty));

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
    public async Task Production_performance_answers_the_erp_order_with_what_stands()
    {
        var plant = await PlantAsync();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE core.work_order SET origin = 'erp', external_system = 'SAP', external_key = '45-' || @s WHERE id = @Id", new { s = _s, Id = plant.Order });
        }

        var started = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE core.job_run SET started_at = started_at - interval '1 hour' WHERE id = @Id", new { Id = started.RunId });
        }

        var first = await db.SendAsync(new ReportProductionCommand(plant.Extrude, null, 60m, 5m, null, plant.Scrap, plant.Staging));
        var second = await db.SendAsync(new ReportProductionCommand(plant.Extrude, null, 30m, 0m, null, null, plant.Staging));
        var count = await ScalarAsync<Guid>(
            "SELECT id FROM core.reason_code WHERE site_id IS NULL AND reason_kind = 'inventory_adjustment' AND code = 'COUNT'", new { });
        await db.SendAsync(new ReverseProductionReportCommand(plant.Extrude, second.ReportId, count, "Counted twice"));

        var document = await QueryAsync<GetJobProductionPerformanceQuery, ProductionPerformanceDocument>(new GetJobProductionPerformanceQuery(plant.Extrude));
        Assert.Equal("J10-" + _s, document.JobNumber);

        System.Xml.Linq.XNamespace ns = ProductionPerformanceB2mml.Ns;
        var response = System.Xml.Linq.XDocument.Parse(document.Xml).Root!.Element(ns + "ProductionResponse")!;
        Assert.Equal(("45-" + _s, "Running"), ((string)response.Element(ns + "ProductionRequestID")!, (string)response.Element(ns + "ResponseState")!));
        var segment = response.Element(ns + "SegmentResponse")!;
        Assert.Equal("10", (string)segment.Element(ns + "ProcessSegmentID")!);
        Assert.Equal(["60", "5"], segment.Elements(ns + "SegmentData").Select(d => (string)d.Element(ns + "Value")!.Element(ns + "ValueString")!));

        // The reversed report's lot is gone; the first report's lot stands with what it made.
        var material = Assert.Single(segment.Elements(ns + "MaterialActual"));
        Assert.Equal(("Produced", first.Lot!.LotNumber, "60"),
            ((string)material.Element(ns + "MaterialUse")!, (string?)material.Element(ns + "MaterialLotID"),
             (string)material.Element(ns + "Quantity")!.Element(ns + "QuantityString")!));

        // Only production jobs have production performance.
        Guid cleaning = Guid.CreateVersion7();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.job (id, site_id, job_number, job_type, equipment_id, status, planned_duration)
                VALUES (@cleaning, @site, 'CLN-' || @s, 'cleaning', @line, 'planned', interval '1 hour')
                """,
                new { cleaning, site = plant.Site, s = _s, line = plant.Packer });
        }

        await Assert.ThrowsAsync<Pulaski.Application.Errors.ValidationException>(() =>
            QueryAsync<GetJobProductionPerformanceQuery, ProductionPerformanceDocument>(new GetJobProductionPerformanceQuery(cleaning)));
    }

    [Fact]
    public async Task A_b2mml_feed_queues_what_changes_a_production_job_under_the_job_and_sends_its_sync()
    {
        var plant = await PlantAsync();
        var relay = db.Services.GetRequiredService<Pulaski.Infrastructure.Integrations.DeliveryRelay>();
        var feed = "perf-" + _s.ToLowerInvariant();
        await db.SendAsync(new Pulaski.Application.Integrations.CreateDeliveryFeedCommand(feed, "ERP performance", "webhook", "b2mml"));
        var settings = await QueryAsync<Pulaski.Application.Integrations.GetDeliveryFeedSettingsQuery, Pulaski.Contracts.Integrations.DeliveryFeedSettingsDto>(
            new Pulaski.Application.Integrations.GetDeliveryFeedSettingsQuery(feed));
        Assert.Equal(("b2mml", 0), (settings.Format, settings.EventKinds.Count));
        var sink = "webhook:" + feed;
        var triggers = Pulaski.Domain.Integrations.DeliveryFormats.PerformanceTriggers;
        await relay.CaptureAsync(sink, triggers, fromNow: true, 1000, CancellationToken.None, jobSubjects: true);

        // Started, reported (job events), and the lot it made (a lot event naming the job): all under the job. A cleaning job isn't taken.
        var started = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        Guid cleaning = Guid.CreateVersion7();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE core.job_run SET started_at = started_at - interval '1 hour' WHERE id = @Id", new { Id = started.RunId });
            await connection.ExecuteAsync(
                """
                INSERT INTO core.job (id, site_id, job_number, job_type, equipment_id, status, planned_duration, dispatched_at)
                VALUES (@cleaning, @site, 'CLN-' || @s, 'cleaning', @line, 'dispatched', interval '1 hour', now())
                """,
                new { cleaning, site = plant.Site, s = _s, line = plant.Packer });
        }

        await db.SendAsync(new StartJobCommand(cleaning, await VersionAsync(cleaning)));
        await db.SendAsync(new ReportProductionCommand(plant.Extrude, null, 50m, 0m, null, null, plant.Staging));
        await relay.CaptureAsync(sink, triggers, fromNow: true, 1000, CancellationToken.None, jobSubjects: true);

        var due = (await relay.DueAsync(sink, TestTime.Now.AddYears(1), 1000, CancellationToken.None)).ToList();
        Assert.All(due, d => Assert.Equal((plant.Extrude, "job"), (d.Subject, d.SubjectType)));
        Assert.Contains(due, d => d.Type == "pulaski.job.started.v1");

        // Held behind each other as one subject: only the first is due; the list names the job.
        var listed = await QueryAsync<Pulaski.Application.Integrations.ListDeliveriesQuery, IReadOnlyList<Pulaski.Contracts.Integrations.DeliveryDto>>(
            new Pulaski.Application.Integrations.ListDeliveriesQuery(Feed: feed) { Search = "J10-" + _s });
        Assert.Contains(listed, d => d.EventType == "pulaski.job.production_reported.v1");
        Assert.Contains(listed, d => d.EventType == "pulaski.lot.produced.v1");
        Assert.All(listed, d => Assert.Equal("J10-" + _s, d.JobNumber));
        Assert.Single(due);

        // What it sends: the job's sync, keyed by the event.
        var document = await QueryAsync<GetJobProductionPerformanceQuery, ProductionPerformanceDocument>(
            new GetJobProductionPerformanceQuery(plant.Extrude, due[0].EventId));
        System.Xml.Linq.XNamespace ns = ProductionPerformanceB2mml.Ns;
        var root = System.Xml.Linq.XDocument.Parse(document.Xml).Root!;
        Assert.Equal(ns + "SyncProductionPerformance", root.Name);
        Assert.Equal(due[0].EventId.ToString(), (string)root.Element(ns + "ApplicationArea")!.Element(ns + "BODID")!);
        Assert.Equal("J10-" + _s, (string)root.Element(ns + "DataArea")!.Element(ns + "ProductionPerformance")!.Element(ns + "ID")!);
    }

    [Fact]
    public async Task Reports_fill_the_lines_lot_until_it_is_full_and_an_operator_can_start_a_new_one()
    {
        var plant = await PlantAsync();
        var capabilityId = await ScalarAsync<Guid>("SELECT id FROM core.equipment_capability WHERE equipment_id = @Line", new { Line = plant.Extruder });
        var from = await ScalarAsync<DateTimeOffset>("SELECT effective_from FROM core.equipment_capability WHERE id = @Id", new { Id = capabilityId });
        var version = await ScalarAsync<long>("SELECT xmin::text::bigint FROM core.equipment_capability WHERE id = @Id", new { Id = capabilityId });
        await db.SendAsync(new UpdateCapabilityCommand(
            capabilityId, version, 100m, db.UomId, null, null, false, from, null, LotRule: new Pulaski.Domain.Inventory.LotCutRule(100m, null, false)));
        Assert.Equal(100m, await ScalarAsync<decimal>("SELECT lot_size FROM core.equipment_capability WHERE id = @Id", new { Id = capabilityId }));

        var started = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE core.job_run SET started_at = started_at - interval '3 hours' WHERE id = @Id", new { Id = started.RunId });
        }

        Task<ReportProductionResponse> Report(decimal good, string? lotNumber = null) =>
            db.SendAsync(new ReportProductionCommand(plant.Extrude, null, good, 0m, null, null, plant.Staging, lotNumber));
        Task<decimal> QuantityAsync(Guid lot) => ScalarAsync<decimal>("SELECT quantity FROM core.lot WHERE id = @Id", new { Id = lot });

        // 60 then 30 into one lot; 25 fills it to 100 and starts a second with 15.
        var first = await Report(60m);
        var second = await Report(30m);
        Assert.Equal(first.Lot!.LotId, second.Lot!.LotId);
        var third = await Report(25m);
        Assert.Equal(2, third.Lots.Count);
        Assert.Equal(first.Lot.LotId, third.Lots[0].LotId);
        Assert.Equal((100m, 15m), (await QuantityAsync(first.Lot.LotId), await QuantityAsync(third.Lots[1].LotId)));
        Assert.Contains(third.Warnings, w => w.Code == "lot_cut" && w.Message.Contains("is full", StringComparison.Ordinal));

        // The job page shows the rule and the lot being filled: the second, at staging.
        var page = await QueryAsync<GetJobExecutionQuery, JobExecutionDto>(new GetJobExecutionQuery(plant.Extrude));
        Assert.Equal((100m, (decimal?)null, false), (page.LotSize!.Value, page.LotHours, page.LotByShift));
        var filling = Assert.Single(page.FillingLots);
        Assert.Equal((third.Lots[1].LotId, 15m), (filling.LotId, filling.Quantity));

        // Reversing the 30 takes only its share back; the full lot isn't reopened, as the second is now the latest.
        var count = await ScalarAsync<Guid>(
            "SELECT id FROM core.reason_code WHERE site_id IS NULL AND reason_kind = 'inventory_adjustment' AND code = 'COUNT'", new { });
        await db.SendAsync(new ReverseProductionReportCommand(plant.Extrude, second.ReportId, count, "Counted twice"));
        Assert.Equal(70m, await QuantityAsync(first.Lot.LotId));
        var fourth = await Report(10m);
        Assert.Equal((third.Lots[1].LotId, 25m), (fourth.Lot!.LotId, await QuantityAsync(third.Lots[1].LotId)));

        // The operator starts a new lot: the next report goes into a third one. A number typed by hand still makes its own lot.
        var closed = await db.SendAsync(new StartNewLotCommand(plant.Extrude, "New pallet"));
        Assert.Equal([third.Lots[1].LotNumber!], closed.ClosedLots);
        var fifth = await Report(5m);
        Assert.NotEqual(third.Lots[1].LotId, fifth.Lot!.LotId);
        var typed = await Report(5m, "HAND-" + _s);
        Assert.Equal("HAND-" + _s, typed.Lot!.LotNumber);
        Assert.Equal(5m, await QuantityAsync(fifth.Lot.LotId));
        await Assert.ThrowsAsync<DomainRuleException>(async () =>
        {
            await db.SendAsync(new StartNewLotCommand(plant.Extrude));
            await db.SendAsync(new StartNewLotCommand(plant.Extrude));
        });

        // A material that isn't lot-tracked can't have lots cut.
        var bulk = await db.SendAsync(new CreateMaterialCommand("BULK-" + _s, "Regrind", null, "raw", null, db.UomId, false, null));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new AddCapabilityCommand(
            bulk.Id, plant.Packer, 100m, db.UomId, null, null, false, null, null, LotRule: new Pulaski.Domain.Inventory.LotCutRule(null, 8m, false))));
    }

    [Fact]
    public async Task After_a_report_that_starts_several_lots_the_next_fills_the_last_one()
    {
        var plant = await PlantAsync();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE core.equipment_capability SET lot_size = 100 WHERE equipment_id = @Line", new { Line = plant.Extruder });
        }

        var started = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE core.job_run SET started_at = started_at - interval '5 hours' WHERE id = @Id", new { Id = started.RunId });
        }

        // One report, three lots made in one transaction: 100, 100 and 50. The last one made is the one being filled, whatever their times and ids.
        var big = await db.SendAsync(new ReportProductionCommand(plant.Extrude, null, 250m, 0m, null, null, plant.Staging));
        Assert.Equal(3, big.Lots.Count);
        Assert.Contains(big.Warnings, w => w.Code == "lot_cut" && w.Message.Contains("3 new lots", StringComparison.Ordinal));
        var page = await QueryAsync<GetJobExecutionQuery, JobExecutionDto>(new GetJobExecutionQuery(plant.Extrude));
        Assert.Equal(big.Lots[2].LotId, Assert.Single(page.FillingLots).LotId);

        var next = await db.SendAsync(new ReportProductionCommand(plant.Extrude, null, 30m, 0m, null, null, plant.Staging));
        Assert.Equal(big.Lots[2].LotId, Assert.Single(next.Lots).LotId);
        Assert.Equal(80m, await ScalarAsync<decimal>("SELECT quantity FROM core.lot WHERE id = @Id", new { Id = big.Lots[2].LotId }));

        // Filled to its size, it is off the page: the next report starts a new lot.
        await db.SendAsync(new ReportProductionCommand(plant.Extrude, null, 20m, 0m, null, null, plant.Staging));
        Assert.Empty((await QueryAsync<GetJobExecutionQuery, JobExecutionDto>(new GetJobExecutionQuery(plant.Extrude))).FillingLots);
    }

    [Fact]
    public async Task A_line_board_shows_the_lines_in_its_scope_with_their_running_and_next_jobs()
    {
        var plant = await PlantAsync();
        await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));

        // The whole site: both lines; the extruder running Extrude, the packer with Pack next. Both track machine state and no machine is
        // connected here, so the board says so rather than "running".
        var site = await QueryAsync<GetLineDisplayQuery, LineDisplayDto>(new GetLineDisplayQuery(plant.Site, plant.Site, "Hall A"));
        Assert.Equal("Hall A", site.Title);
        Assert.Equal(["EXT-" + _s, "PCK-" + _s], site.Lines.Select(l => l.Code).Order(StringComparer.Ordinal));
        var extruder = Assert.Single(site.Lines, l => l.LineId == plant.Extruder);
        Assert.Equal(("J10-" + _s, "no_signal"), (extruder.Running!.JobNumber, extruder.Status));
        var packer = Assert.Single(site.Lines, l => l.LineId == plant.Packer);
        Assert.Equal(("J20-" + _s, "no_signal"), (packer.Next!.JobNumber, packer.Status));
        Assert.Null(packer.Running);

        // A terminal scoped to the packer sees only it; ?line= narrows the site to one line, in any case.
        Assert.Equal([plant.Packer], (await QueryAsync<GetLineDisplayQuery, LineDisplayDto>(new GetLineDisplayQuery(plant.Site, plant.Packer, "Pack"))).Lines.Select(l => l.LineId));
        Assert.Equal([plant.Extruder], (await QueryAsync<GetLineDisplayQuery, LineDisplayDto>(
            new GetLineDisplayQuery(plant.Site, plant.Site, "Hall A", ("ext-" + _s).ToLowerInvariant()))).Lines.Select(l => l.LineId));
    }

    [Fact]
    public async Task A_station_shows_its_line_and_whats_next_and_offers_the_lines_when_it_has_no_line()
    {
        var plant = await PlantAsync();
        var terminal = new Pulaski.Application.Labor.TerminalRecord(
            Guid.CreateVersion7(), plant.Site, "ST-" + _s, "Extruder station", plant.Site, plant.Extruder, "badge", true, TestTime.Now);

        // Bound to its first line offered: the extruder, with Extrude next; no one signed on.
        var station = await QueryAsync<GetStationQuery, StationDto>(new GetStationQuery(terminal, null, null));
        Assert.Equal(("EXT-" + _s, "J10-" + _s, (string?)null), (station.Line!.Code, Assert.Single(station.Next).JobNumber, station.PersonName));
        Assert.True(station.IdleMinutes > 0);

        // Asked for the packer; and with no first line, it offers both.
        Assert.Equal("PCK-" + _s, (await QueryAsync<GetStationQuery, StationDto>(new GetStationQuery(terminal, null, "pck-" + _s.ToLowerInvariant()))).Line!.Code);
        var unbound = await QueryAsync<GetStationQuery, StationDto>(new GetStationQuery(terminal with { DefaultEquipmentId = null }, null, null));
        Assert.Null(unbound.Line);
        Assert.Equal(2, unbound.Lines.Count);
    }

    [Fact]
    public async Task At_a_station_a_badged_person_acts_with_their_users_role_and_someone_without_a_user_only_records_time()
    {
        var plant = await PlantAsync();
        var terminal = new Pulaski.Application.Labor.TerminalRecord(
            Guid.CreateVersion7(), plant.Site, "ST-" + _s, "Extruder station", plant.Site, plant.Extruder, "badge", true, TestTime.Now);
        await db.SendAsync(new Pulaski.Application.Security.CreateUserCommand(
            "op." + _s, "Olu " + _s, null, "Temporary-pass-1", Pulaski.Domain.Security.Roles.Operator, plant.Site));
        await db.SendAsync(new Pulaski.Application.Security.CreateUserCommand(
            "pl." + _s, "Pia " + _s, null, "Temporary-pass-1", Pulaski.Domain.Security.Roles.Planner, plant.Site));
        CreatedResponse operatorPerson = await db.SendAsync(new Pulaski.Application.Labor.CreatePersonCommand(plant.Site, "800-" + _s, "Olu " + _s, null, "op." + _s));
        CreatedResponse plannerPerson = await db.SendAsync(new Pulaski.Application.Labor.CreatePersonCommand(plant.Site, "801-" + _s, "Pia " + _s, null, "pl." + _s));
        CreatedResponse noUser = await db.SendAsync(new Pulaski.Application.Labor.CreatePersonCommand(plant.Site, "802-" + _s, "Noa " + _s, null, null));

        // The operator's role runs jobs, reports and codes stops here; the access is the same the signed-in app uses.
        var olu = await QueryAsync<GetStationQuery, StationDto>(new GetStationQuery(terminal, operatorPerson.Id, null));
        Assert.Equal(("Olu " + _s, true, true, true, true), (olu.PersonName, olu.PersonIsUser, olu.CanRunJobs, olu.CanReport, olu.CanCodeStops));
        var security = db.Services.GetRequiredService<Pulaski.Application.Security.ISecurityReadStore>();
        var access = await security.GetPersonAccessAsync(operatorPerson.Id, TestTime.Now, CancellationToken.None);
        Assert.Equal(("op." + _s).ToLowerInvariant(), access!.Username);

        // A planner's role does none of them on the floor; someone without a user only records time.
        var pia = await QueryAsync<GetStationQuery, StationDto>(new GetStationQuery(terminal, plannerPerson.Id, null));
        Assert.Equal((true, false, false, false), (pia.PersonIsUser, pia.CanRunJobs, pia.CanReport, pia.CanCodeStops));
        var noa = await QueryAsync<GetStationQuery, StationDto>(new GetStationQuery(terminal, noUser.Id, null));
        Assert.Equal((false, false, false, false), (noa.PersonIsUser, noa.CanRunJobs, noa.CanReport, noa.CanCodeStops));
        Assert.Null(await security.GetPersonAccessAsync(noUser.Id, TestTime.Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_paused_job_frees_the_line_and_the_site_policy_sets_the_tolerance()
    {
        var plant = await PlantAsync();
        var policy = await QueryAsync<GetExecutionPolicyQuery, ExecutionPolicyDto>(new GetExecutionPolicyQuery(plant.Site));
        Assert.Equal((true, 110m), (policy.IsDefault, policy.RateTolerancePct));
        var set = await db.SendAsync(new SetExecutionPolicyCommand(plant.Site, policy.Version, 150m, 120m));
        var saved = await QueryAsync<GetExecutionPolicyQuery, ExecutionPolicyDto>(new GetExecutionPolicyQuery(plant.Site));
        Assert.Equal((150m, false, set.Version), (saved.RateTolerancePct, saved.IsDefault, saved.Version));
        await Assert.ThrowsAsync<Application.Errors.ConflictException>(() => db.SendAsync(new SetExecutionPolicyCommand(plant.Site, 0, 120m, 120m)));

        var started = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        await db.SendAsync(new PauseJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        Assert.NotNull(await ScalarAsync<DateTimeOffset?>("SELECT ended_at FROM core.job_run WHERE id = @Id", new { Id = started.RunId }));
        Assert.Equal("paused", await ScalarAsync<string>("SELECT status FROM core.job WHERE id = @Id", new { Id = plant.Extrude }));

        var resumed = await db.SendAsync(new StartJobCommand(plant.Extrude, await VersionAsync(plant.Extrude)));
        Assert.Equal(2, await ScalarAsync<int>("SELECT run_no FROM core.job_run WHERE id = @Id", new { Id = resumed.RunId }));
    }

    [Fact]
    public async Task Batches_are_reported_whole_and_a_finished_goods_partial_waits_for_approval()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "BE-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "BS-" + _s, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        CreatedResponse mixer = await db.SendAsync(new CreateEquipmentCommand(site.Id, "MIX-" + _s, "Mixer", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse staging = await db.SendAsync(new CreateEquipmentCommand(site.Id, "BST-" + _s, "Staging", null, "work_center", "storage_zone", false, false, true, null));
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("FG-" + _s, "Bagged blend", null, "finished", null, db.UomId, true, null));
        var materialVersion = await ScalarAsync<long>("SELECT xmin::text::bigint FROM core.material WHERE id = @Id", new { Id = material.Id });
        await db.SendAsync(new SetBatchingCommand(material.Id, materialVersion, 50m, 2m, null));
        Assert.Equal("exact", await ScalarAsync<string>("SELECT batch_rule FROM core.material WHERE id = @Id", new { Id = material.Id }));

        Guid order = Guid.CreateVersion7(), job = Guid.CreateVersion7();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
                VALUES (@mixer, @material, 200, @uom, now() - interval '1 day');
                INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, status)
                VALUES (@order, @site, 'BWO-' || @s, @material, 500, @uom, 'firmed');
                INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id, status)
                VALUES (@job, @site, @order, 'BJ-' || @s, 'production', @mixer, @material, 500, @uom, 'firmed');
                """,
                new { s = _s, mixer = mixer.Id, material = material.Id, uom = db.UomId, order, site = site.Id, job });
        }

        var started = await db.SendAsync(new StartJobCommand(job, await VersionAsync(job)));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE core.job_run SET started_at = started_at - interval '1 hour' WHERE id = @Id", new { Id = started.RunId });
        }

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new ReportProductionCommand(job, null, 0m, 0m, null, null, staging.Id) { GoodBatches = [49.3m] }));
        var batches = await db.SendAsync(new ReportProductionCommand(job, null, 0m, 0m, null, null, staging.Id) { GoodBatches = [50m, 50m] });
        Assert.Equal(2, batches.Lots.Count);

        var breakdown = await ScalarAsync<Guid>("SELECT id FROM core.reason_code WHERE reason_kind = 'run_end' AND code = 'BREAKDOWN' AND site_id IS NULL", new { });
        await Assert.ThrowsAsync<Application.Errors.ValidationException>(async () =>
            await db.SendAsync(new PauseJobCommand(job, await VersionAsync(job), PartialBatch: new PartialBatchInput(30m, staging.Id, null))));
        var paused = await db.SendAsync(new PauseJobCommand(job, await VersionAsync(job), breakdown, "Mixer tripped", new PartialBatchInput(30m, staging.Id, null)));
        Assert.Contains(paused.Notes, n => n.Contains("on hold for approval", StringComparison.Ordinal));

        var pending = Assert.Single(await QueryAsync<ListPendingPartialBatchesQuery, IReadOnlyList<PartialBatchDto>>(new ListPendingPartialBatchesQuery(site.Id)));
        Assert.Equal(("pending", 30m), (pending.Status, pending.Quantity));
        Assert.Equal("on_hold", await ScalarAsync<string>("SELECT status FROM core.lot WHERE id = @Id", new { Id = pending.LotId }));
        Assert.Equal(130m, await ScalarAsync<decimal>("SELECT good_qty FROM core.job_run WHERE id = @Id", new { Id = started.RunId }));

        await Assert.ThrowsAsync<Application.Errors.ForbiddenException>(() => db.SendAsync(new ApprovePartialBatchCommand(pending.Id, "My own")));
        await db.SendAsAsync("quality-user", new ApprovePartialBatchCommand(pending.Id, "Sold as a sample"));
        Assert.Equal("available", await ScalarAsync<string>("SELECT status FROM core.lot WHERE id = @Id", new { Id = pending.LotId }));
        Assert.Empty(await QueryAsync<ListPendingPartialBatchesQuery, IReadOnlyList<PartialBatchDto>>(new ListPendingPartialBatchesQuery(site.Id)));
        var detail = await QueryAsync<GetJobExecutionQuery, JobExecutionDto>(new GetJobExecutionQuery(job));
        Assert.Equal(("approved", 50m, true), (Assert.Single(detail.PartialBatches).Status, detail.BatchSize!.Value, detail.Runs[0].HasPartialBatch));
    }

    [Fact]
    public async Task A_line_with_its_own_batch_size_reports_in_its_batches_and_takes_only_whole_ones()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "LE-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "LS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse big = await db.SendAsync(new CreateEquipmentCommand(site.Id, "BIG-" + _s, "Big mixer", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse staging = await db.SendAsync(new CreateEquipmentCommand(site.Id, "LST-" + _s, "Staging", null, "work_center", "storage_zone", false, false, true, null));
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("BL-" + _s, "Blend", null, "intermediate", null, db.UomId, true, null));

        // Not made in batches yet: a line can't have a batch size of its own.
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(
            new AddCapabilityCommand(material.Id, big.Id, 400m, db.UomId, null, null, false, TestTime.Now.AddDays(-1), null, BatchSize: 200m)));
        var materialVersion = await ScalarAsync<long>("SELECT xmin::text::bigint FROM core.material WHERE id = @Id", new { Id = material.Id });
        await db.SendAsync(new SetBatchingCommand(material.Id, materialVersion, 50m, 2m, null));
        var from = TestTime.Now.AddDays(-1);
        var capability = await db.SendAsync(
            new AddCapabilityCommand(material.Id, big.Id, 400m, db.UomId, null, null, false, from, null, BatchSize: 200m));
        Assert.Contains(capability.Notes, n => n.Contains("batches of 200", StringComparison.Ordinal));

        Guid order = Guid.CreateVersion7(), job = Guid.CreateVersion7();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, status)
                VALUES (@order, @site, 'LWO-' || @s, @material, 400, @uom, 'firmed');
                INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id, status)
                VALUES (@job, @site, @order, 'LJ-' || @s, 'production', @big, @material, 400, @uom, 'firmed');
                """,
                new { s = _s, big = big.Id, material = material.Id, uom = db.UomId, order, site = site.Id, job });
        }

        var started = await db.SendAsync(new StartJobCommand(job, await VersionAsync(job)));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE core.job_run SET started_at = started_at - interval '1 hour' WHERE id = @Id", new { Id = started.RunId });
        }

        // The material's 50 isn't a batch on this line; its 200 is.
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new ReportProductionCommand(job, null, 0m, 0m, null, null, staging.Id) { GoodBatches = [50m] }));
        var reported = await db.SendAsync(new ReportProductionCommand(job, null, 0m, 0m, null, null, staging.Id) { GoodBatches = [200m] });
        Assert.Single(reported.Lots);
        Assert.Equal(200m, (await QueryAsync<GetJobExecutionQuery, JobExecutionDto>(new GetJobExecutionQuery(job))).BatchSize!.Value);

        // The line's own tolerance (#85): 190 is 5% short, not a batch on the material's 2%, but a batch on the line's 10%.
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new ReportProductionCommand(job, null, 0m, 0m, null, null, staging.Id) { GoodBatches = [190m] }));
        var loosened = await db.SendAsync(new UpdateCapabilityCommand(
            capability.Id, capability.Version, 400m, db.UomId, null, null, false, from, null, BatchSize: 200m, BatchTolerancePct: 10m));
        Assert.Contains(loosened.Notes, n => n.Contains("10% from its size", StringComparison.Ordinal));
        await db.SendAsync(new ReportProductionCommand(job, null, 0m, 0m, null, null, staging.Id) { GoodBatches = [190m] });
        Assert.Equal(10m, (await QueryAsync<GetJobExecutionQuery, JobExecutionDto>(new GetJobExecutionQuery(job))).BatchTolerancePct);
    }

    [Fact]
    public async Task A_replacement_for_exactly_what_is_left_reports_its_partial_batch_and_the_order_adds_up()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "PE-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "PS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse mixer = await db.SendAsync(new CreateEquipmentCommand(site.Id, "PMIX-" + _s, "Mixer", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse staging = await db.SendAsync(new CreateEquipmentCommand(site.Id, "PST-" + _s, "Staging", null, "work_center", "storage_zone", false, false, true, null));
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("PB-" + _s, "Blend", null, "intermediate", null, db.UomId, true, null));
        var materialVersion = await ScalarAsync<long>("SELECT xmin::text::bigint FROM core.material WHERE id = @Id", new { Id = material.Id });
        await db.SendAsync(new SetBatchingCommand(material.Id, materialVersion, 50m, 2m, null));

        Guid order = Guid.CreateVersion7(), job = Guid.CreateVersion7();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
                VALUES (@mixer, @material, 400, @uom, now() - interval '1 day');
                INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, status)
                VALUES (@order, @site, 'PWO-' || @s, @material, 175, @uom, 'firmed');
                INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id, status)
                VALUES (@job, @site, @order, 'PJ-' || @s, 'production', @mixer, @material, 175, @uom, 'firmed');
                """,
                new { s = _s, mixer = mixer.Id, material = material.Id, uom = db.UomId, order, site = site.Id, job });
        }

        async Task StartAndBackdateAsync(Guid id)
        {
            var started = await db.SendAsync(new StartJobCommand(id, await VersionAsync(id)));
            await using var connection = await db.AdminDataSource.OpenConnectionAsync();
            await connection.ExecuteAsync("UPDATE core.job_run SET started_at = started_at - interval '1 hour' WHERE id = @Id", new { Id = started.RunId });
        }

        await StartAndBackdateAsync(job);
        await db.SendAsync(new ReportProductionCommand(job, null, 0m, 0m, null, null, staging.Id) { GoodBatches = [50m, 50m] });
        var cancelled = await db.SendAsync(new CancelJobCommand(job, await VersionAsync(job), "Mixer down", Replace: true, ReplaceExact: true));
        var replacement = cancelled.ReplacementJobId!.Value;
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            // The first run, ended by the cancel, moved earlier so the replacement's run can start an hour ago.
            await connection.ExecuteAsync(
                "UPDATE core.job_run SET started_at = now() - interval '3 hours', ended_at = now() - interval '2 hours' WHERE job_id = @Id", new { Id = job });
        }

        Assert.Equal((75m, true), (
            await ScalarAsync<decimal>("SELECT planned_qty FROM core.job WHERE id = @Id", new { Id = replacement }),
            await ScalarAsync<bool>("SELECT partial_batch_planned FROM core.job WHERE id = @Id", new { Id = replacement })));

        // Its last batch is a partial, reported as it is made; the order's jobs add up to what it planned.
        await StartAndBackdateAsync(replacement);
        var reported = await db.SendAsync(new ReportProductionCommand(replacement, null, 0m, 0m, null, null, staging.Id) { GoodBatches = [50m], PartialQty = 25m });
        Assert.Equal(2, reported.Lots.Count);
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*)::int FROM core.partial_batch WHERE job_id = @Id", new { Id = replacement }));
        Assert.Equal(175m, await ScalarAsync<decimal>(
            "SELECT sum(r.good_qty) FROM core.job_run r JOIN core.job j ON j.id = r.job_id WHERE j.work_order_id = @Id", new { Id = order }));
    }
}
