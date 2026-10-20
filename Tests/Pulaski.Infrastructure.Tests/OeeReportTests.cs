using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Inventory;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.Tests;

/// <summary>The utilization and OEE report end to end (D-043): history written by ingest and reports, read with real SQL as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class OeeReportTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task A_lines_stops_runs_and_reports_become_utilization_and_oee()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "OENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "OS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "OL-" + _s, "Pack line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse filler = await db.SendAsync(new CreateEquipmentCommand(line.Id, "OF-" + _s, "Filler", null, "work_unit", "machine", true, false, false, null));
        CreatedResponse jam = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "OJAM-" + _s, "Jam", false, 10, UtilizationClasses.Downtime));

        var lineNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(line.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(line.Id, lineNow.Version, null, StateDerivations.Series, null, 300));
        var fillerNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(filler.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(filler.Id, fillerNow.Version, null, StateDerivations.OwnTags, null, 120));

        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "OG-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "B", "Broker", EdgeProtocols.SparkplugB, new Dictionary<string, string> { ["host"] = "b" }));
        CreatedResponse state = await db.SendAsync(new CreateEquipmentTagCommand(filler.Id, TagRoles.State, connection.Id, "P/N/OF:State", 1, 0, null));

        var now = DateTimeOffset.UtcNow;
        var t0 = now.AddMinutes(-40);
        EdgeSignalDto S(long seq, int minutes, decimal value) => new() { Sequence = seq, TagId = state.Id, OccurredAt = t0.AddMinutes(minutes), Value = value };
        await db.SendAsync(new IngestEdgeSignalsCommand(node.Id, Guid.CreateVersion7(), null,
        [
            S(1, 0, 6),
            S(2, 5, 2), S(3, 15, 6),     // a 10-minute stop, coded as a jam below
            S(4, 20, 5), S(5, 21, 6),    // a 1-minute micro-stop
        ]));
        var stop = (await QueryAsync<ListStopsQuery, IReadOnlyList<DowntimeDto>>(new ListStopsQuery(new StopFilter(site.Id, filler.Id, t0, now.AddMinutes(1), true, false)))).Single();
        await db.SendAsync(new CodeDowntimeCommand(stop.Id, stop.StartedAt, jam.Id, null));

        // A job ran on the line for the 30 minutes from t0 at an ideal 600/h, and 270 were reported, 20 of them scrap.
        Guid order = Guid.CreateVersion7(), job = Guid.CreateVersion7(), run = Guid.CreateVersion7();
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            await admin.ExecuteAsync(
                """
                INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
                VALUES (@line, @material, 600, @uom, @t0 - interval '1 day');
                INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, status)
                VALUES (@order, @site, 'OWO-' || @s, @material, 1000, @uom, 'completed');
                INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id, status)
                VALUES (@job, @site, @order, 'OJ-' || @s, 'production', @line, @material, 1000, @uom, 'completed');
                INSERT INTO core.job_run (id, job_id, equipment_id, run_no, started_at, ended_at, status, good_qty, scrap_qty, uom_id)
                VALUES (@run, @job, @line, 1, @t0, @t0 + interval '30 minutes', 'ended', 250, 20, @uom);
                INSERT INTO ts.production_count (id, occurred_at, equipment_id, run_id, job_id, count_kind, delta_qty, uom_id, source)
                VALUES (gen_random_uuid(), @t0 + interval '25 minutes', @line, @run, @job, 'processed', 270, @uom, 'operator'),
                       (gen_random_uuid(), @t0 + interval '25 minutes', @line, @run, @job, 'defective', 20, @uom, 'operator'),
                       (gen_random_uuid(), @t0 + interval '25 minutes', @line, @run, @job, 'processed', 999, @uom, 'machine');
                """,
                new { s = _s, site = site.Id, line = line.Id, material = db.MaterialId, uom = db.UomId, order, job, run, t0 });
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var report = await QueryAsync<GetOeeReportQuery, OeeReportDto>(new GetOeeReportQuery(site.Id, today.AddDays(-1), today, null));

        Assert.Equal(["OL-" + _s, "OF-" + _s], report.Equipment.Select(e => e.Code));
        var fill = report.Equipment[1].Figures;
        Assert.Equal((1, 1), (fill.Stops, fill.MicroStops));
        var jammed = Assert.Single(report.Utilization, u => u.EquipmentId == filler.Id && u.Use == "down" && u.ReasonCodeId == jam.Id);
        Assert.Equal(("OJAM-" + _s, 0.17m, 1), (jammed.ReasonCode, jammed.Hours, jammed.Stops));
        Assert.Contains(report.Utilization, u => u.EquipmentId == filler.Id && u.Use == "micro_stop" && u.Stops == 1);

        // The line in series takes the filler's time; machine counts aren't reports.
        var product = Assert.Single(report.Products);
        Assert.Equal((250m, 20m, 1), (product.GoodQty, product.ScrapQty, product.Runs));
        var lineFigures = report.Equipment[0].Figures;
        Assert.Equal(0.17m, jammed.Hours);
        Assert.Equal(92.6m, lineFigures.QualityPct);
        // 20 of the run's 30 minutes were run time (10 jammed): 270 at 600/h is 27 earned minutes of 20 → 135%.
        Assert.Equal(135m, lineFigures.PerformancePct);
        Assert.Empty(report.Notes);
        Assert.NotEmpty(report.Shifts);
    }
}
