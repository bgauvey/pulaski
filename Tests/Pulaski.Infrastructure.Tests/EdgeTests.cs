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

/// <summary>Edge nodes, tags and machine data ingest end to end (D-041): real SQL, as the application login.</summary>
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
}
