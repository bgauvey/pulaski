using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.MachineData;
using Pulaski.Application.Overview;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Overview;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain.MachineData;
using Pulaski.Domain.Shifts;

namespace Pulaski.Infrastructure.Tests;

/// <summary>The Overview's live figures end to end (D-070): machine history, reports and held lots, read with real SQL as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class OverviewTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task Each_line_shows_its_state_and_its_shift_so_far_and_a_line_without_state_shows_none()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "VENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "VS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "VL1-" + _s, "Pack line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse filler = await db.SendAsync(new CreateEquipmentCommand(line.Id, "VF-" + _s, "Filler", null, "work_unit", "machine", true, false, false, null));
        CreatedResponse manual = await db.SendAsync(new CreateEquipmentCommand(site.Id, "VL2-" + _s, "Manual line", null, "work_center", "production_line", false, true, false, null));

        // An 8-hour shift that started two hours ago (on the hour), every day; the site follows it.
        var now = DateTimeOffset.UtcNow;
        var shiftStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(-2);
        CreatedResponse schedule = await db.SendAsync(new CreateShiftScheduleCommand(site.Id, "V8", "Eights", null, 1, DateOnly.FromDateTime(shiftStart.UtcDateTime).AddDays(-10),
            [new ShiftDto { Code = "A", Name = "Early", Start = shiftStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), Minutes = 480, Days = [0] }]));
        var siteVersion = (await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(site.Id))).Version;
        await db.SendAsync(new SetEquipmentCalendarCommand(site.Id, siteVersion, CalendarModes.Schedule, schedule.Id));

        var lineNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(line.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(line.Id, lineNow.Version, null, StateDerivations.Series, null, 300));
        var fillerNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(filler.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(filler.Id, fillerNow.Version, null, StateDerivations.OwnTags, null, 120));
        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "VG-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "B", "Broker", EdgeProtocols.SparkplugB, new Dictionary<string, string> { ["host"] = "b" }));
        CreatedResponse state = await db.SendAsync(new CreateEquipmentTagCommand(filler.Id, TagRoles.State, connection.Id, "P/N/VF:State", 1, 0, null));

        // From 40 minutes ago: executing, a 10-minute stop nobody codes, then executing again.
        var t0 = now.AddMinutes(-40);
        EdgeSignalDto S(long seq, int minutes, decimal value) => new() { Sequence = seq, TagId = state.Id, OccurredAt = t0.AddMinutes(minutes), Value = value };
        await db.SendAsync(new IngestEdgeSignalsCommand(node.Id, Guid.CreateVersion7(), null, [S(1, 0, 6), S(2, 5, 2), S(3, 15, 6)]));

        // 270 reported on the line in the shift, 20 of them scrap, and a machine count that isn't a report; one lot on hold.
        Guid order = Guid.CreateVersion7(), job = Guid.CreateVersion7(), run = Guid.CreateVersion7();
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            await admin.ExecuteAsync(
                """
                INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, status)
                VALUES (@order, @site, 'VWO-' || @s, @material, 1000, @uom, 'in_progress');
                INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id, status)
                VALUES (@job, @site, @order, 'VJ-' || @s, 'production', @line, @material, 1000, @uom, 'running');
                INSERT INTO core.job_run (id, job_id, equipment_id, run_no, started_at, status, good_qty, scrap_qty, uom_id)
                VALUES (@run, @job, @line, 1, @t0, 'running', 250, 20, @uom);
                INSERT INTO ts.production_count (id, occurred_at, equipment_id, run_id, job_id, count_kind, delta_qty, uom_id, source)
                VALUES (gen_random_uuid(), @t0 + interval '25 minutes', @line, @run, @job, 'processed', 270, @uom, 'operator'),
                       (gen_random_uuid(), @t0 + interval '25 minutes', @line, @run, @job, 'defective', 20, @uom, 'operator'),
                       (gen_random_uuid(), @t0 + interval '25 minutes', @line, @run, @job, 'processed', 999, @uom, 'machine');
                INSERT INTO core.lot (site_id, lot_number, material_id, status, quantity, uom_id, origin)
                VALUES (@site, 'VHOLD-' || @s, @material, 'on_hold', 5, @uom, 'received'),
                       (@site, 'VGONE-' || @s, @material, 'on_hold', 0, @uom, 'received');
                """,
                new { s = _s, site = site.Id, line = line.Id, material = db.MaterialId, uom = db.UomId, order, job, run, t0 });
        }

        var overview = await QueryAsync<GetOverviewQuery, OverviewDto>(new GetOverviewQuery(site.Id));

        // The strip covers the shift; lines by code.
        Assert.Equal((shiftStart, shiftStart.AddHours(8), true), (overview.WindowStart, overview.WindowEnd, overview.WindowIsShift));
        Assert.Equal(["VL1-" + _s, "VL2-" + _s], overview.Lines.Select(l => l.Code));
        Assert.Equal((1, 0), (overview.LotsOnHold, overview.LotsInQuarantine));

        var pack = overview.Lines[0];
        Assert.Equal((true, "A", shiftStart, "producing"), (pack.TracksState, pack.Shift?.ShiftCode, pack.From, pack.Category));
        var output = Assert.Single(pack.Output);
        Assert.Equal((250m, 20m), (output.GoodQty, output.ScrapQty));

        // Before the first signal the filler had none: down, no signal. The stop is down, not coded, and counts once.
        Assert.Equal(1, pack.UncodedStops);
        var notCoded = Assert.Single(pack.DownReasons, r => r.ReasonCode is null && !r.NoSignal);
        Assert.Equal(("Not coded", 10m), (notCoded.Name, notCoded.Minutes));
        Assert.Contains(pack.DownReasons, r => r.NoSignal);
        Assert.Equal(pack.DownReasons.Sum(r => r.Minutes), pack.DownMinutes);
        Assert.Equal(["no_signal", "run", "down", "run"], pack.States.Select(s => s.Use));
        Assert.Equal((shiftStart, overview.AsOf), (pack.States[0].Start, pack.States[^1].End));

        // A line that doesn't report its state: unavailable, not zero.
        var hand = overview.Lines[1];
        Assert.Equal((false, (string?)null, (decimal?)null, (int?)null), (hand.TracksState, hand.Category, hand.DownMinutes, hand.UncodedStops));
        Assert.Empty(hand.States);
        Assert.Equal("A", hand.Shift?.ShiftCode);
    }
}
