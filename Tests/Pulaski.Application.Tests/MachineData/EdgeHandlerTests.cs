using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.MachineData;

/// <summary>Edge nodes, connections, tags and machine data ingest (D-050).</summary>
public class EdgeHandlerTests
{
    private static readonly DateTimeOffset T0 = HandlerFixture.Now;

    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _packer;

    public EdgeHandlerTests()
    {
        Edge.AddSite(_site);
        // A controller numbering its states 1 Stopped, 2 Running, 3 Starved.
        var profile = Edge.AddProfile(new() { [1] = 2, [2] = 6, [3] = 5 });
        _packer = Edge.AddEquipment(_site, "PACK-01", profileId: profile);
    }

    private InMemoryEdge Edge => _f.Stores.Edge;

    private static CancellationToken Ct => CancellationToken.None;

    private static Dictionary<string, string> Broker => new() { ["host"] = "broker.local", ["port"] = "1883", ["groupId"] = "Plant1" };

    private async Task<(Guid Node, Guid Connection)> NodeAsync()
    {
        var node = await new CreateEdgeNodeHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateEdgeNodeCommand(_site, "gw-1", "Gateway 1", null), Ct);
        var connection = await new CreateEdgeConnectionHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateEdgeConnectionCommand(node.Id, "broker1", "Line broker", EdgeProtocols.SparkplugB, Broker), Ct);
        return (node.Id, connection.Id);
    }

    private Task<CreatedResponse> TagAsync(Guid connection, string role, string address, Guid? equipment = null, decimal scale = 1, long? rollover = null) =>
        new CreateEquipmentTagHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateEquipmentTagCommand(equipment ?? _packer, role, connection, address, scale, 0, rollover), Ct);

    private IngestEdgeSignalsHandler Ingest() => new(_f.Stores.UnitOfWork, Edge, new FixedRequestContext("edge-gw-1"), _f.Ids, _f.Time);

    private static EdgeSignalDto Signal(long sequence, Guid tag, int minutes, decimal? value, bool good = true) =>
        new() { Sequence = sequence, TagId = tag, OccurredAt = T0.AddMinutes(minutes), Value = value, Good = good };

    [Fact]
    public async Task A_node_and_its_connection_are_checked_and_coded_in_upper_case()
    {
        var (node, connection) = await NodeAsync();

        Assert.Equal("GW-1", Edge.Node(node).Value.Code);
        Assert.Equal(EventTypes.EdgeConnectionCreated, _f.Stores.Events.Appended[^1].Type);
        await Assert.ThrowsAsync<ConflictException>(() => new CreateEdgeNodeHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateEdgeNodeCommand(_site, "GW-1", "Again", null), Ct));

        var bad = new Dictionary<string, string> { ["port"] = "99999", ["tls"] = "maybe", ["colour"] = "red" };
        var error = await Assert.ThrowsAsync<ValidationException>(() => new CreateEdgeConnectionHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateEdgeConnectionCommand(node, "B2", "Bad", EdgeProtocols.SparkplugB, bad), Ct));
        Assert.Equal(4, error.Errors["Settings"].Length);
        Assert.NotEqual(Guid.Empty, connection);
    }

    [Fact]
    public async Task Deleting_a_node_deactivates_its_connections_and_tags_and_is_idempotent()
    {
        var (node, connection) = await NodeAsync();
        var state = await TagAsync(connection, TagRoles.State, "Plant1/Packing/PACK-01:State");
        var count = await TagAsync(connection, TagRoles.ProcessedCount, "Plant1/Packing/PACK-01:Count");

        await new DeleteEdgeNodeHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
            .HandleAsync(new DeleteEdgeNodeCommand(node), Ct);

        Assert.False(Edge.Node(node).Value.IsActive);
        Assert.False(Edge.Connection(connection).Value.IsActive);
        Assert.False(Edge.Tag(state.Id).Value.IsActive);
        Assert.False(Edge.Tag(count.Id).Value.IsActive);
        Assert.Equal(EventTypes.EdgeNodeDeleted, _f.Stores.Events.Appended[^1].Type);
        Assert.Equal(2, _f.Stores.Events.Appended.Count(e => e.Type == EventTypes.EquipmentTagDeactivated));
        Assert.Single(_f.Stores.Events.Appended, e => e.Type == EventTypes.EdgeConnectionDeleted);

        var eventCount = _f.Stores.Events.Appended.Count;
        await new DeleteEdgeNodeHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
            .HandleAsync(new DeleteEdgeNodeCommand(node), Ct);
        Assert.Equal(eventCount, _f.Stores.Events.Appended.Count);
    }

    [Fact]
    public async Task Deleting_a_tag_physically_removes_it_whether_active_or_inactive()
    {
        var (node, connection) = await NodeAsync();
        var state = await TagAsync(connection, TagRoles.State, "Plant1/Packing/PACK-01:State");
        var stop = await TagAsync(connection, TagRoles.StopReason, "Plant1/Packing/PACK-01:Stop");
        await Edge.UpdateTagAsync(Edge.Tag(stop.Id).Value with { IsActive = false }, T0, Ct);

        await new DeleteEquipmentTagHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
            .HandleAsync(new DeleteEquipmentTagCommand(state.Id), Ct);

        Assert.False(Edge.HasTag(state.Id));
        Assert.True(Edge.HasTag(stop.Id));
        Assert.True(Edge.Connection(connection).Value.IsActive);
        Assert.True(Edge.Node(node).Value.IsActive);
        Assert.Equal(EventTypes.EquipmentTagDeleted, _f.Stores.Events.Appended[^1].Type);

        await new DeleteEquipmentTagHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
            .HandleAsync(new DeleteEquipmentTagCommand(stop.Id), Ct);
        Assert.False(Edge.HasTag(stop.Id));
        Assert.Equal(2, _f.Stores.Events.Appended.Count(e => e.Type == EventTypes.EquipmentTagDeleted));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            new DeleteEquipmentTagHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
                .HandleAsync(new DeleteEquipmentTagCommand(state.Id), Ct));
    }

    [Fact]
    public async Task Deleting_a_missing_node_or_tag_returns_not_found()
    {
        var missingId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            new DeleteEdgeNodeHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
                .HandleAsync(new DeleteEdgeNodeCommand(missingId), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            new DeleteEquipmentTagHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
                .HandleAsync(new DeleteEquipmentTagCommand(missingId), Ct));
    }

    [Fact]
    public async Task A_tag_binds_a_Sparkplug_address_to_equipment_of_the_site_that_tracks_state()
    {
        var (_, connection) = await NodeAsync();

        await TagAsync(connection, TagRoles.State, "Plant1/Packing/PACK-01:Status/StateCurrent");
        Assert.Equal(EventTypes.EquipmentTagCreated, _f.Stores.Events.Appended[^1].Type);

        await Assert.ThrowsAsync<ValidationException>(() => TagAsync(connection, TagRoles.UnitMode, "Plant1/Pack#ing:Status/StateCurrent"));
        await Assert.ThrowsAsync<ValidationException>(() => TagAsync(connection, TagRoles.UnitMode, "just-a-name"));
        await Assert.ThrowsAsync<ValidationException>(() => TagAsync(connection, TagRoles.Alarm, "Plant1/Packing:Admin/Alarm"));
        await Assert.ThrowsAsync<ValidationException>(() => TagAsync(connection, TagRoles.State, "Plant1/Packing:X", rollover: 10));

        var rack = Edge.AddEquipment(_site, "RACK", tracksState: false);
        await Assert.ThrowsAsync<DomainRuleException>(() => TagAsync(connection, TagRoles.State, "Plant1/Packing:Rack", rack));
        var otherSite = Guid.CreateVersion7();
        var elsewhere = Edge.AddEquipment(otherSite, "FAR");
        await Assert.ThrowsAsync<DomainRuleException>(() => TagAsync(connection, TagRoles.State, "Plant1/Packing:Far", elsewhere));
    }

    [Fact]
    public async Task Signals_become_live_state_state_changes_and_counts_through_the_profile()
    {
        var (node, connection) = await NodeAsync();
        var state = (await TagAsync(connection, TagRoles.State, "Plant1/Packing/PACK-01:State")).Id;
        var stop = (await TagAsync(connection, TagRoles.StopReason, "Plant1/Packing/PACK-01:StopReason")).Id;
        var count = (await TagAsync(connection, TagRoles.ProcessedCount, "Plant1/Packing/PACK-01:Count", scale: 12, rollover: 1000)).Id;
        var run = Edge.OpenRunOn(_packer);
        var stream = Guid.CreateVersion7();

        var result = await Ingest().HandleAsync(new IngestEdgeSignalsCommand(node, stream, "1.0.0",
        [
            Signal(1, state, 0, 2),         // Running: Execute
            Signal(2, count, 0, 990),       // baseline
            Signal(3, count, 5, 995),       // 5 cases of 12
            Signal(4, state, 6, 1),         // Stopped
            Signal(5, stop, 6, 207),
            Signal(6, count, 7, 3),         // wrapped at 1000: 8 more
            Signal(7, state, 9, 3),         // Starved: Suspended
            Signal(8, state, 10, null, good: false),
        ]), Ct);

        Assert.Equal((8L, 8, 0), (result.LastSequence, result.Applied, result.Skipped));
        Assert.Equal([6, 2, 5, 0], Edge.StateChanges.Select(c => c.Change.PackMlState));
        Assert.All(Edge.StateChanges, c => Assert.Equal(run, c.RunId));
        Assert.True(Edge.StateChanges[^1].Change.SignalLost);
        Assert.Equal((int?)207, Edge.StateChanges[2].Change.StopReason);
        Assert.Equal([60m, 96m], Edge.Counts.Select(c => c.Quantity));
        Assert.All(Edge.Counts, c => Assert.Equal(("processed", (Guid?)run), (c.CountKind, c.RunId)));

        var live = Edge.Live(_packer)!;
        Assert.Equal((0, (int?)null, (int?)207), (live.PackMlState, live.RawState, live.StopReason));
        var seen = Edge.Node(node).Value;
        Assert.Equal((stream, 8L, "1.0.0"), (seen.StreamId, seen.LastSequence, seen.AgentVersion));
    }

    [Fact]
    public async Task A_batch_sent_again_changes_nothing_and_a_new_stream_starts_again()
    {
        var (node, connection) = await NodeAsync();
        var state = (await TagAsync(connection, TagRoles.State, "Plant1/Packing/PACK-01:State")).Id;
        var stream = Guid.CreateVersion7();
        var batch = new IngestEdgeSignalsCommand(node, stream, null, [Signal(1, state, 0, 2), Signal(2, state, 1, 1)]);

        await Ingest().HandleAsync(batch, Ct);
        var again = await Ingest().HandleAsync(batch, Ct);

        Assert.Equal((0, 2, 2L), (again.Applied, again.Skipped, again.LastSequence));
        Assert.Equal(2, Edge.StateChanges.Count);

        // The edge lost its buffer: a new stream, numbered from 1 again.
        var fresh = await Ingest().HandleAsync(new IngestEdgeSignalsCommand(node, Guid.CreateVersion7(), null, [Signal(1, state, 2, 2)]), Ct);
        Assert.Equal((1, 1L), (fresh.Applied, fresh.LastSequence));
        Assert.Equal(3, Edge.StateChanges.Count);
    }

    [Fact]
    public async Task Signals_for_unbound_tags_are_skipped_and_a_switched_off_node_is_refused()
    {
        var (node, connection) = await NodeAsync();
        var state = (await TagAsync(connection, TagRoles.State, "Plant1/Packing/PACK-01:State")).Id;
        var current = Edge.Node(node);
        await new UpdateEquipmentTagHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
            .HandleAsync(new UpdateEquipmentTagCommand(state, (await Edge.GetTagForUpdateAsync(state, Ct))!.Version, "Plant1/Packing/PACK-01:State", 1, 0, null, IsActive: false), Ct);

        var result = await Ingest().HandleAsync(new IngestEdgeSignalsCommand(node, Guid.CreateVersion7(), null, [Signal(1, state, 0, 2), Signal(2, Guid.CreateVersion7(), 0, 1)]), Ct);
        Assert.Equal((0, 2, 2L), (result.Applied, result.Skipped, result.LastSequence));
        Assert.Empty(Edge.StateChanges);

        await new UpdateEdgeNodeHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events)
            .HandleAsync(new UpdateEdgeNodeCommand(node, Edge.Node(node).Version, current.Value.Name, null, IsActive: false), Ct);
        await Assert.ThrowsAsync<DomainRuleException>(() => Ingest().HandleAsync(new IngestEdgeSignalsCommand(node, Guid.CreateVersion7(), null, []), Ct));
    }

    [Fact]
    public async Task Metrics_are_kept_only_for_the_nodes_own_connections()
    {
        var (node, connection) = await NodeAsync();
        var reports = new List<EdgeMetricReportDto>
        {
            new() { ConnectionId = connection, Address = "Plant1/Packing/PACK-01:State", DataType = "Int32", Value = "2", SeenAt = T0 },
            new() { ConnectionId = connection, Address = "Plant1/Packing/PACK-01:State", Value = "1", SeenAt = T0.AddSeconds(5) },
            new() { ConnectionId = Guid.CreateVersion7(), Address = "Elsewhere:X", SeenAt = T0 },
        };

        var kept = await new ReportEdgeMetricsHandler(_f.Stores.UnitOfWork, Edge, _f.Time).HandleAsync(new ReportEdgeMetricsCommand(node, reports), Ct);

        Assert.Equal(1, kept);
        var metric = Assert.Single(Edge.Metrics);
        Assert.Equal(("1", "Int32"), (metric.Value, metric.DataType));
        Assert.Equal(HandlerFixture.Now, Edge.Node(node).Value.LastSeenAt);
    }
}
