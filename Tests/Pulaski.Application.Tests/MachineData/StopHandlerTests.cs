using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.MachineData;

/// <summary>Stops from machine data, coding them, and the machine status board (D-050).</summary>
public class StopHandlerTests
{
    private static readonly DateTimeOffset T0 = HandlerFixture.Now;

    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();

    private static CancellationToken Ct => CancellationToken.None;

    private InMemoryEdge Edge => _f.Stores.Edge;

    private InMemoryStops Stops => _f.Stores.Stops;

    private async Task<(Guid Node, Guid State, Guid Stop, Guid Machine)> MachineAsync(StopReasons reasons)
    {
        Edge.AddSite(_site);
        var machine = Edge.AddEquipment(_site, "PACK-01");
        Edge.SetStopRules(machine, TimeSpan.FromMinutes(5), reasons);
        var node = await new CreateEdgeNodeHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateEdgeNodeCommand(_site, "GW", "Gateway", null), Ct);
        var connection = await new CreateEdgeConnectionHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateEdgeConnectionCommand(node.Id, "B", "Broker", EdgeProtocols.SparkplugB, new Dictionary<string, string> { ["host"] = "b" }), Ct);
        var tag = new CreateEquipmentTagHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Events, _f.Events, _f.Ids);
        var state = await tag.HandleAsync(new CreateEquipmentTagCommand(machine, TagRoles.State, connection.Id, "P/N/D:State", 1, 0, null), Ct);
        var stop = await tag.HandleAsync(new CreateEquipmentTagCommand(machine, TagRoles.StopReason, connection.Id, "P/N/D:Stop", 1, 0, null), Ct);
        return (node.Id, state.Id, stop.Id, machine);
    }

    private Task<EdgeSignalBatchResponse> IngestAsync(Guid node, Guid stream, params EdgeSignalDto[] signals) =>
        new IngestEdgeSignalsHandler(_f.Stores.UnitOfWork, Edge, _f.Stores.Stops, _f.Stores.Events, _f.Events, new FixedRequestContext("edge"), _f.Ids, _f.Time)
            .HandleAsync(new IngestEdgeSignalsCommand(node, stream, null, signals), Ct);

    private static EdgeSignalDto S(long seq, Guid tag, int minutes, decimal value) => new() { Sequence = seq, TagId = tag, OccurredAt = T0.AddMinutes(minutes), Value = value };

    [Fact]
    public async Task Machine_data_opens_a_stop_maps_it_by_its_code_and_closes_it_when_the_machine_runs()
    {
        var jam = Guid.CreateVersion7();
        var (node, state, stop, machine) = await MachineAsync(new StopReasons(new Dictionary<int, Guid?> { [101] = jam }, new Dictionary<int, Guid>()));
        var stream = Guid.CreateVersion7();

        // Running, stopped at 08:01 with code 101 a moment after; still stopped at the end of the batch.
        await IngestAsync(node, stream, S(1, state, 0, 6), S(2, state, 1, 2), S(3, stop, 1, 101));
        var open = Assert.Single(Edge.Stops.Values);
        Assert.Equal((T0.AddMinutes(1), (DateTimeOffset?)null, (int?)101, (Guid?)jam), (open.StartedAt, open.EndedAt, open.PlcStopReason, open.MappedReasonCodeId));

        // Running again at 08:11 in the next batch: the stop closes, ten minutes long, not a micro-stop.
        await IngestAsync(node, stream, S(4, state, 11, 6));
        var closed = Assert.Single(Edge.Stops.Values);
        Assert.Equal((T0.AddMinutes(11), false), (closed.EndedAt, closed.IsMicroStop));

        // A short one is a micro-stop.
        await IngestAsync(node, stream, S(5, state, 20, 5), S(6, state, 22, 6));
        Assert.True(Edge.Stops.Values.Single(s => s.StartedAt == T0.AddMinutes(20)).IsMicroStop);
        Assert.Equal(machine, closed.EquipmentId);
    }

    private (Guid Machine, Guid Stop, Guid Jam, Guid Other) CodingSetup(bool micro = false)
    {
        var machine = Guid.CreateVersion7();
        var stop = Guid.CreateVersion7();
        var jam = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        Stops.AddStop(new StopRecord(stop, T0, T0.AddMinutes(10), machine, "PACK-01", jam, null, micro, null));
        Stops.AddReason(machine, new ReasonCodeDto { Id = jam, Kind = "utilization", Code = "JAM", Name = "Jam", UtilizationClass = "downtime" });
        Stops.AddReason(machine, new ReasonCodeDto { Id = other, Kind = "utilization", Code = "OTHER", Name = "Other", RequiresComment = true, UtilizationClass = "downtime" });
        return (machine, stop, jam, other);
    }

    private CodeDowntimeHandler Code(string actor = "oli") =>
        new(_f.Stores.UnitOfWork, Stops, _f.Stores.Events, _f.Events, new FixedRequestContext(actor), _f.Time);

    [Fact]
    public async Task An_operator_confirms_or_changes_a_stops_reason_and_both_are_kept()
    {
        var (_, stop, jam, other) = CodingSetup();

        await Code().HandleAsync(new CodeDowntimeCommand(stop, T0, jam, null), Ct);
        Assert.Equal((jam, "oli"), (Stops.Coded[stop].ReasonCodeId, Stops.Coded[stop].CodedBy));
        Assert.Equal(EventTypes.DowntimeCoded, _f.Stores.Events.Appended[^1].Type);

        await Assert.ThrowsAsync<ValidationException>(() => Code().HandleAsync(new CodeDowntimeCommand(stop, T0, other, " "), Ct));
        await Code("sam").HandleAsync(new CodeDowntimeCommand(stop, T0, other, "Wrong carton size"), Ct);
        Assert.Equal((other, "Wrong carton size", "sam"), Stops.Coded[stop]);
    }

    [Fact]
    public async Task Only_an_offered_reason_codes_a_stop_that_isnt_a_micro_stop()
    {
        var (_, stop, _, _) = CodingSetup();
        await Assert.ThrowsAsync<DomainRuleException>(() => Code().HandleAsync(new CodeDowntimeCommand(stop, T0, Guid.CreateVersion7(), null), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Code().HandleAsync(new CodeDowntimeCommand(stop, T0.AddSeconds(1), Guid.CreateVersion7(), null), Ct));

        var (_, micro, jam, _) = CodingSetup(micro: true);
        await Assert.ThrowsAsync<DomainRuleException>(() => Code().HandleAsync(new CodeDowntimeCommand(micro, T0, jam, null), Ct));
    }

    private static MachineStatusDto Status(string code, string? category, Guid? parent = null, string derivation = "own_tags", int? state = null) => new()
    {
        EquipmentId = Guid.CreateVersion7(),
        Code = code,
        Name = code,
        ParentId = parent,
        EquipmentRole = parent is null ? "production_line" : "machine",
        StateDerivation = derivation,
        Category = category,
        PackMlState = state,
    };

    [Fact]
    public async Task A_line_takes_its_state_from_its_machines_as_it_is_set_to()
    {
        var series = Status("LINE-S", null, derivation: StateDerivations.Series);
        var parallel = Status("LINE-P", null, derivation: StateDerivations.Parallel);
        var constraint = Status("LINE-C", null, derivation: StateDerivations.ConstraintMachine);
        var filler = Status("FILL", OeeCategories.Producing, series.EquipmentId, state: 6);
        var capper = Status("CAP", OeeCategories.StoppedFaulted, series.EquipmentId, state: 2);
        var a = Status("A", OeeCategories.WaitingExternal, parallel.EquipmentId);
        var b = Status("B", OeeCategories.Producing, parallel.EquipmentId);
        var c = Status("C", OeeCategories.HeldInternal, constraint.EquipmentId, state: 11);
        foreach (var s in new[] { series, filler, capper, parallel, a, b })
        {
            Stops.AddStatus(s);
        }

        Stops.AddStatus(constraint, c.EquipmentId);
        Stops.AddStatus(c);

        var board = (await new GetMachineStatusHandler(Stops, _f.Time).HandleAsync(new GetMachineStatusQuery(_site), Ct)).ToDictionary(m => m.Code);

        Assert.Equal((OeeCategories.StoppedFaulted, true), (board["LINE-S"].Category, board["LINE-S"].IsDerived));
        Assert.Equal(OeeCategories.Producing, board["LINE-P"].Category);
        Assert.Equal((OeeCategories.HeldInternal, (int?)11), (board["LINE-C"].Category, board["LINE-C"].PackMlState));
        Assert.False(board["FILL"].IsDerived);
    }
}
