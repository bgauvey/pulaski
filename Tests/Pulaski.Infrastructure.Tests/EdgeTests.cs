using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Edge nodes, tags and machine data ingest end to end (D-050): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class EdgeTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task An_edge_node_is_configured_and_its_signals_land_as_state_changes_counts_and_live_state()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "EENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "ES-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "EL-" + _s, "Pack line", null, "work_center", "production_line", true, true, false, null));

        // A profile whose controller sends 2 for Execute and 1 for Stopped, with stop code 207.
        CreatedResponse profile = await db.SendAsync(new CreateMachineProfileCommand(site.Id, "EP-" + _s, "Packer", null, null, null));
        var csv = MachineProfileCsv.Parse("kind,value,name,packml_state\nstate,1,Stopped,Stopped\nstate,2,Running,Execute\nstop_reason,207,Glue low,\n");
        await db.SendAsync(new ImportMachineProfileCommand(profile.Id, profile.Version, csv, DryRun: false));
        var lineNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(line.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(line.Id, lineNow.Version, profile.Id, StateDerivations.OwnTags, null, 300));

        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "GW-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "BROKER", "Broker", EdgeProtocols.SparkplugB,
            new Dictionary<string, string> { ["host"] = "localhost", ["groupId"] = "Plant1" }));
        var address = $"Plant1/Packing/EL-{_s}:";
        CreatedResponse state = await db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.State, connection.Id, address + "Status/StateCurrent", 1, 0, null));
        CreatedResponse stop = await db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.StopReason, connection.Id, address + "Admin/StopReason", 1, 0, null));
        CreatedResponse count = await db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.ProcessedCount, connection.Id, address + "Admin/ProdProcessedCount", 1, 0, null));
        await Assert.ThrowsAsync<Pulaski.Application.Errors.ConflictException>(() =>
            db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.State, connection.Id, address + "Other", 1, 0, null)));

        var config = await QueryAsync<GetEdgeConfigQuery, EdgeConfigDto>(new GetEdgeConfigQuery(node.Id));
        Assert.Equal((3, "Plant1", (Guid?)null), (config.Tags.Count, Assert.Single(config.Connections).Settings["groupId"], config.StreamId));

        var t0 = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
        var stream = Guid.CreateVersion7();
        EdgeSignalDto S(long seq, Guid tag, int minutes, decimal? value) => new() { Sequence = seq, TagId = tag, OccurredAt = t0.AddMinutes(minutes), Value = value };
        var batch = new IngestEdgeSignalsCommand(node.Id, stream, "1.0.0",
            [S(1, state.Id, 0, 2), S(2, count.Id, 0, 100), S(3, count.Id, 4, 140), S(4, state.Id, 5, 1), S(5, stop.Id, 5, 207)]);
        var result = await db.SendAsync(batch);
        Assert.Equal((5L, 5), (result.LastSequence, result.Applied));
        Assert.Equal(0, (await db.SendAsync(batch)).Applied);

        await using (var connectionDb = await db.AdminDataSource.OpenConnectionAsync())
        {
            var changes = (await connectionDb.QueryAsync<(short State, int? Raw, string Source)>(
                "SELECT packml_state, raw_state, source FROM ts.equipment_state_change WHERE equipment_id = @Id ORDER BY occurred_at", new { line.Id })).ToList();
            Assert.Equal([((short)6, (int?)2, "packtags"), ((short)2, (int?)1, "packtags")], changes);
            var counted = await connectionDb.ExecuteScalarAsync<decimal>(
                "SELECT sum(delta_qty) FROM ts.production_count WHERE equipment_id = @Id AND source = 'machine'", new { line.Id });
            Assert.Equal(40m, counted);
        }

        await db.SendAsync(new ReportEdgeMetricsCommand(node.Id,
            [new EdgeMetricReportDto { ConnectionId = connection.Id, Address = address + "Status/StateCurrent", DataType = "Int32", Value = "1", SeenAt = t0.AddMinutes(5) },
             new EdgeMetricReportDto { ConnectionId = connection.Id, Address = address + "Status/CurMachSpeed", DataType = "Float", Value = "61.5", SeenAt = t0.AddMinutes(5) }]));

        var detail = await QueryAsync<GetEdgeNodeQuery, EdgeNodeDetailDto>(new GetEdgeNodeQuery(node.Id));
        var live = Assert.Single(detail.Live);
        Assert.Equal((2, "Stopped", (int?)1, (int?)207, "Glue low"), (live.PackMlState, live.PackMlName, live.RawState, live.StopReason, live.StopReasonText));
        Assert.Equal((5L, "1.0.0", 3), (detail.Node.LastSequence, detail.Node.AgentVersion, detail.Node.TagCount));
        Assert.Equal(2, detail.Metrics.Count);
        Assert.Equal(state.Id, detail.Metrics.Single(m => m.Address.EndsWith("StateCurrent", StringComparison.Ordinal)).TagId);
        Assert.Equal("1", detail.Tags.Single(t => t.Id == state.Id).LastValue);
        Assert.Single(await QueryAsync<ListEdgeNodesQuery, IReadOnlyList<EdgeNodeDto>>(new ListEdgeNodesQuery(site.Id)));
    }

    [Fact]
    public async Task Alarm_and_warning_tags_raise_and_clear_alarms_and_process_values_are_kept_scaled()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "AENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "AS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "AL-" + _s, "Extruder", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse profile = await db.SendAsync(new CreateMachineProfileCommand(site.Id, "AP-" + _s, "Extruder", null, null, null));
        var csv = MachineProfileCsv.Parse("kind,value,name,packml_state\nalarm,12,Melt pressure high,\nalarm,7,Screen pack blocked,\nwarning,3,Barrel zone 2 slow to heat,\n");
        await db.SendAsync(new ImportMachineProfileCommand(profile.Id, profile.Version, csv, DryRun: false));
        var lineNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(line.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(line.Id, lineNow.Version, profile.Id, StateDerivations.OwnTags, null, 300));

        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "AG-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "BROKER", "Broker", EdgeProtocols.SparkplugB,
            new Dictionary<string, string> { ["host"] = "localhost" }));
        var address = $"Plant1/Extrusion/AL-{_s}:";

        // Two alarm words and a warning on one machine (#26); a melt temperature in tenths of a degree, with a 0.5 °C deadband.
        CreatedResponse alarms1 = await db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.Alarm, connection.Id, address + "Admin/Alarm/0/ID", 1, 0, null));
        CreatedResponse alarms2 = await db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.Alarm, connection.Id, address + "Admin/Alarm/1/ID", 1, 0, null));
        CreatedResponse warning = await db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.Warning, connection.Id, address + "Admin/Warning/0/ID", 1, 0, null));
        CreatedResponse melt = await db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.ProcessValue, connection.Id, address + "Melt/Temp", 0.1m, 0, null, 0.5m));
        await Assert.ThrowsAsync<Pulaski.Application.Errors.ValidationException>(() =>
            db.SendAsync(new CreateEquipmentTagCommand(line.Id, TagRoles.State, connection.Id, address + "State", 1, 0, null, 1m)));

        var config = await QueryAsync<GetEdgeConfigQuery, EdgeConfigDto>(new GetEdgeConfigQuery(node.Id));
        Assert.Equal(5m, config.Tags.Single(t => t.Id == melt.Id).Deadband);   // 0.5 °C in tenths
        Assert.Null(config.Tags.Single(t => t.Id == alarms1.Id).Deadband);

        var t0 = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
        EdgeSignalDto S(long seq, Guid tag, int seconds, decimal? value, bool good = true) =>
            new() { Sequence = seq, TagId = tag, OccurredAt = t0.AddSeconds(seconds), Value = value, Good = good };
        var batch = new IngestEdgeSignalsCommand(node.Id, Guid.CreateVersion7(), "1.0.0",
        [
            S(1, alarms1.Id, 0, 12), S(2, alarms2.Id, 5, 7), S(3, warning.Id, 10, 3),
            S(4, alarms1.Id, 20, 12),           // the same alarm again: nothing new
            S(5, alarms2.Id, 30, null, false),  // signal lost: the alarm stays
            S(6, alarms1.Id, 40, 0),            // cleared
            S(7, melt.Id, 0, 2105), S(8, melt.Id, 60, 2112), S(9, melt.Id, 120, null, false),
        ]);
        Assert.Equal(9, (await db.SendAsync(batch)).Applied);
        Assert.Equal(0, (await db.SendAsync(batch)).Applied);

        var listed = await QueryAsync<ListAlarmsQuery, AlarmListDto>(new ListAlarmsQuery(new AlarmFilter(site.Id, null, t0, t0.AddHours(1), ActiveOnly: false)));
        Assert.False(listed.IsTruncated);
        Assert.Equal(
            [("warning", 3, "Barrel zone 2 slow to heat", false), ("alarm", 7, "Screen pack blocked", false), ("alarm", 12, "Melt pressure high", true)],
            listed.Alarms.Select(a => (a.Kind, a.Number, a.Message, a.ClearedAt is not null)));
        var cleared = listed.Alarms.Single(a => a.Number == 12);
        Assert.Equal((t0, t0.AddSeconds(40), 40, address + "Admin/Alarm/0/ID"), (cleared.RaisedAt, cleared.ClearedAt, cleared.DurationSeconds, cleared.Address));

        var active = await QueryAsync<ListAlarmsQuery, AlarmListDto>(new ListAlarmsQuery(new AlarmFilter(site.Id, line.Id, t0, t0.AddHours(1), ActiveOnly: true)));
        Assert.Equal([3, 7], active.Alarms.Select(a => a.Number));

        // A later batch clears the second word's alarm and raises another on it.
        await db.SendAsync(new IngestEdgeSignalsCommand(node.Id, Guid.CreateVersion7(), "1.0.0", [S(1, alarms2.Id, 90, 12)]));
        var after = await QueryAsync<ListAlarmsQuery, AlarmListDto>(new ListAlarmsQuery(new AlarmFilter(site.Id, line.Id, t0, t0.AddHours(1), ActiveOnly: true)));
        Assert.Equal([(12, address + "Admin/Alarm/1/ID"), (3, address + "Admin/Warning/0/ID")], after.Alarms.Select(a => (a.Number, a.Address)));

        var series = Assert.Single(await QueryAsync<GetProcessValuesQuery, IReadOnlyList<ProcessValueSeriesDto>>(
            new GetProcessValuesQuery(line.Id, null, t0, t0.AddHours(1))));
        Assert.Equal((melt.Id, address + "Melt/Temp", false), (series.TagId, series.Address, series.IsTruncated));
        Assert.Equal([(t0, (double?)210.5, true), (t0.AddSeconds(60), 211.2, true), (t0.AddSeconds(120), null, false)],
            series.Points.Select(p => (p.At, p.Value.HasValue ? Math.Round(p.Value.Value, 3) : (double?)null, p.Good)));
    }

    [Fact]
    public async Task Process_values_older_than_the_retention_are_dropped_a_month_at_a_time()
    {
        await using var admin = await db.AdminDataSource.OpenConnectionAsync();
        await admin.ExecuteAsync("SELECT ts.create_month_partitions('ts.process_value'::regclass, DATE '2020-01-01', 1)");
        var maintenance = db.Services.GetRequiredService<Pulaski.Infrastructure.Persistence.DatabaseMaintenance>();

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => maintenance.DropOldProcessValuesAsync(3, CancellationToken.None));
        Assert.True(await maintenance.DropOldProcessValuesAsync(90, CancellationToken.None) >= 1);
        Assert.Null(await admin.ExecuteScalarAsync<string?>("SELECT to_regclass('ts.process_value_p202001')::text"));
        Assert.NotNull(await admin.ExecuteScalarAsync<string?>(
            "SELECT to_regclass('ts.process_value_p' || to_char(current_date, 'YYYYMM'))::text"));
    }

    [Fact]
    public async Task Deleting_an_edge_node_deactivates_its_connections_and_tags()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "DE-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "DS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "DL-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "DG-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "DB-" + _s, "Broker", EdgeProtocols.SparkplugB,
            new Dictionary<string, string> { ["host"] = "localhost" }));
        CreatedResponse tag = await db.SendAsync(new CreateEquipmentTagCommand(
            line.Id, TagRoles.State, connection.Id, $"Plant1/Area/DL-{_s}:Status/StateCurrent", 1, 0, null));

        await db.SendAsync(new DeleteEdgeNodeCommand(node.Id));

        await using var connectionDb = await db.AdminDataSource.OpenConnectionAsync();
        var active = await connectionDb.QuerySingleAsync<(bool Node, bool Connection, bool Tag)>(
            """
            SELECT n.is_active AS Node, c.is_active AS Connection, t.is_active AS Tag
            FROM core.edge_node n
            JOIN core.edge_connection c ON c.edge_node_id = n.id
            JOIN core.equipment_tag t ON t.connection_id = c.id
            WHERE n.id = @NodeId AND c.id = @ConnectionId AND t.id = @TagId
            """,
            new { NodeId = node.Id, ConnectionId = connection.Id, TagId = tag.Id });
        Assert.Equal((false, false, false), active);

        var config = await QueryAsync<GetEdgeConfigQuery, EdgeConfigDto>(new GetEdgeConfigQuery(node.Id));
        Assert.False(config.IsActive);
        Assert.Empty(config.Connections);
        Assert.Empty(config.Tags);
    }

    [Fact]
    public async Task Deleting_tags_removes_the_rows_and_detaches_quality_sources()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "TE-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "TS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "TL-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "TG-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "TB-" + _s, "Broker", EdgeProtocols.SparkplugB,
            new Dictionary<string, string> { ["host"] = "localhost" }));
        CreatedResponse tag = await db.SendAsync(new CreateEquipmentTagCommand(
            line.Id, TagRoles.State, connection.Id, $"Plant1/Area/TL-{_s}:Status/StateCurrent", 1, 0, null));
        CreatedResponse inactiveTag = await db.SendAsync(new CreateEquipmentTagCommand(
            line.Id, TagRoles.UnitMode, connection.Id, $"Plant1/Area/TL-{_s}:Status/UnitModeCurrent", 1, 0, null));
        await db.SendAsync(new UpdateEquipmentTagCommand(
            inactiveTag.Id, inactiveTag.Version, $"Plant1/Area/TL-{_s}:Status/UnitModeCurrent", 1, 0, null, IsActive: false));
        var characteristicId = Guid.CreateVersion7();

        await using (var setup = await db.AdminDataSource.OpenConnectionAsync())
        {
            await setup.ExecuteAsync(
                """
                INSERT INTO core.quality_characteristic
                    (id, site_id, code, name, equipment_id, equipment_tag_id, data_kind, chart_type)
                VALUES
                    (@Id, @SiteId, @Code, 'Line speed', @EquipmentId, @TagId, 'variable', 'i_mr')
                """,
                new { Id = characteristicId, SiteId = site.Id, Code = "QC-" + _s, EquipmentId = line.Id, TagId = tag.Id });
        }

        await db.SendAsync(new DeleteEquipmentTagCommand(tag.Id));
        await db.SendAsync(new DeleteEquipmentTagCommand(inactiveTag.Id));

        await using var connectionDb = await db.AdminDataSource.OpenConnectionAsync();
        var state = await connectionDb.QuerySingleAsync<(bool Node, bool Connection, bool ActiveTag, bool InactiveTag, Guid? QualityTag)>(
            """
            SELECT n.is_active AS Node,
                   c.is_active AS Connection,
                   EXISTS (SELECT 1 FROM core.equipment_tag WHERE id = @TagId) AS ActiveTag,
                   EXISTS (SELECT 1 FROM core.equipment_tag WHERE id = @InactiveTagId) AS InactiveTag,
                   q.equipment_tag_id AS QualityTag
            FROM core.edge_node n
            JOIN core.edge_connection c ON c.edge_node_id = n.id AND c.id = @ConnectionId
            JOIN core.quality_characteristic q ON q.id = @CharacteristicId
            WHERE n.id = @NodeId
            """,
            new { NodeId = node.Id, ConnectionId = connection.Id, TagId = tag.Id, InactiveTagId = inactiveTag.Id, CharacteristicId = characteristicId });
        Assert.Equal((true, true, false, false, null), state);

        var detail = await QueryAsync<GetEdgeNodeQuery, EdgeNodeDetailDto>(new GetEdgeNodeQuery(node.Id));
        Assert.Empty(detail.Tags);
        var config = await QueryAsync<GetEdgeConfigQuery, EdgeConfigDto>(new GetEdgeConfigQuery(node.Id));
        Assert.Empty(config.Tags);
        Assert.Single(config.Connections);
    }
}
