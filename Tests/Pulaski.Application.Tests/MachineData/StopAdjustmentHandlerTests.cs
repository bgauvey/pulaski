using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.MachineData;

/// <summary>Entering stops by hand, splitting and merging them (D-069).</summary>
public class StopAdjustmentHandlerTests
{
    private static readonly DateTimeOffset T0 = HandlerFixture.Now.AddHours(-2);

    private readonly HandlerFixture _f = new();
    private readonly Guid _machine = Guid.CreateVersion7();
    private readonly Guid _jam = Guid.CreateVersion7();
    private readonly Guid _setup = Guid.CreateVersion7();
    private readonly Guid _run = Guid.CreateVersion7();

    public StopAdjustmentHandlerTests()
    {
        Stops.AddEquipment(new StopEquipment(_machine, "PACK-01", TracksState: true), _run);
        Stops.AddReason(_machine, new ReasonCodeDto { Id = _jam, Kind = "utilization", Code = "JAM", Name = "Jam", UtilizationClass = UtilizationClasses.Downtime });
        Stops.AddReason(_machine, new ReasonCodeDto { Id = _setup, Kind = "utilization", Code = "SETUP", Name = "Setup", UtilizationClass = UtilizationClasses.Planned });
    }

    private static CancellationToken Ct => CancellationToken.None;

    private InMemoryStops Stops => _f.Stores.Stops;

    private FixedRequestContext Sam { get; } = new("sam");

    private AdjustableStop Stop(int fromMinutes, int? toMinutes, Guid? machine = null, Guid? operatorReason = null)
    {
        var stop = new AdjustableStop(Guid.CreateVersion7(), T0.AddMinutes(fromMinutes), toMinutes is { } m ? T0.AddMinutes(m) : null, machine ?? _machine, _run, 2, 1,
            101, _jam, operatorReason, null, operatorReason is null ? null : "oli", null, operatorReason is null ? null : T0, StopSources.Machine, false);
        Stops.AddAdjustable(stop);
        return stop;
    }

    private Task<StopsAdjustedResponse> EnterAsync(int fromMinutes, int toMinutes, Guid? reason = null, string? comment = "Power cut, no signal") =>
        new EnterStopHandler(_f.Stores.UnitOfWork, Stops, Stops, _f.Stores.Events, _f.Events, _f.Ids, Sam, _f.Time)
            .HandleAsync(new EnterStopCommand(_machine, T0.AddMinutes(fromMinutes), T0.AddMinutes(toMinutes), reason ?? _setup, comment), Ct);

    private Task<StopsAdjustedResponse> SplitAsync(AdjustableStop stop, int atMinutes, string? comment = "Jam, then no cartons") =>
        new SplitStopHandler(_f.Stores.UnitOfWork, Stops, _f.Stores.Events, _f.Events, _f.Ids, Sam, _f.Time)
            .HandleAsync(new SplitStopCommand(stop.Id, stop.StartedAt, T0.AddMinutes(atMinutes), comment), Ct);

    private Task<StopsAdjustedResponse> MergeAsync(params AdjustableStop[] stops) =>
        new MergeStopsHandler(_f.Stores.UnitOfWork, Stops, _f.Stores.Events, _f.Events, _f.Ids, Sam, _f.Time)
            .HandleAsync(new MergeStopsCommand([.. stops.Select(s => s.Key)], "One jam; the machine flickered"), Ct);

    [Fact]
    public async Task A_stop_entered_by_hand_is_closed_coded_with_its_reason_and_attributed()
    {
        Stop(0, 10);

        var entered = await EnterAsync(20, 50);

        var stop = Assert.Single(Stops.Inserted);
        Assert.Equal(entered.Stops[0], stop.Key);
        Assert.Equal((StopSources.Manual, T0.AddMinutes(20), T0.AddMinutes(50), (Guid?)_setup, (Guid?)null), (stop.Source, stop.StartedAt, stop.EndedAt, stop.OperatorReasonCodeId, stop.MappedReasonCodeId));
        Assert.Equal(("sam", "Power cut, no signal", PackMlStates.Undefined, (Guid?)_run), (stop.AdjustedBy, stop.AdjustmentComment, stop.PackMlState, stop.RunId));
        Assert.Equal(EventTypes.DowntimeEntered, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task A_stop_entered_by_hand_needs_a_reason_offered_a_comment_and_free_time_in_the_past()
    {
        Stop(0, 10);
        Stop(60, null);    // open since T0 + 1 h

        await Assert.ThrowsAsync<ValidationException>(() => EnterAsync(20, 30, comment: " "));
        await Assert.ThrowsAsync<DomainRuleException>(() => EnterAsync(20, 30, reason: Guid.CreateVersion7()));
        await Assert.ThrowsAsync<DomainRuleException>(() => EnterAsync(5, 15));          // overlaps the first stop
        await Assert.ThrowsAsync<DomainRuleException>(() => EnterAsync(50, 70));         // overlaps the open one
        await Assert.ThrowsAsync<DomainRuleException>(() => EnterAsync(30, 20));
        await Assert.ThrowsAsync<DomainRuleException>(() => EnterAsync(110, 130));       // ends after now
        await Assert.ThrowsAsync<DomainRuleException>(() => EnterAsync(-32 * 24 * 60, -32 * 24 * 60 + 10));
        Assert.Empty(Stops.Inserted);

        await EnterAsync(10, 60);    // touching both is fine
        Assert.Single(Stops.Inserted);
    }

    [Fact]
    public async Task Equipment_that_doesnt_track_state_has_no_stops_to_enter()
    {
        var unit = Guid.CreateVersion7();
        Stops.AddEquipment(new StopEquipment(unit, "BIN-01", TracksState: false));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            new EnterStopHandler(_f.Stores.UnitOfWork, Stops, Stops, _f.Stores.Events, _f.Events, _f.Ids, Sam, _f.Time)
                .HandleAsync(new EnterStopCommand(unit, T0, T0.AddMinutes(5), _setup, "x"), Ct));
    }

    [Fact]
    public async Task A_split_supersedes_the_stop_with_two_parts_that_keep_its_reasons()
    {
        var stop = Stop(0, 30, operatorReason: _jam);

        var split = await SplitAsync(stop, 10);

        Assert.True(Stops.Adjustable[stop.Id].IsSuperseded);
        Assert.Equal([(T0, T0.AddMinutes(10)), (T0.AddMinutes(10), T0.AddMinutes(30))], Stops.Inserted.Select(s => (s.StartedAt, s.EndedAt)));
        Assert.All(Stops.Inserted, s =>
        {
            Assert.Equal((StopSources.Split, (Guid?)_jam, (Guid?)_jam, (int?)101, "oli"), (s.Source, s.MappedReasonCodeId, s.OperatorReasonCodeId, s.PlcStopReason, s.CodedBy));
            Assert.Equal([stop.Id], s.ReplacesIds!);
            Assert.Equal(("sam", "Jam, then no cartons"), (s.AdjustedBy, s.AdjustmentComment));
        });
        Assert.Equal(Stops.Inserted.Select(s => s.Key), split.Stops);
        Assert.Equal(EventTypes.DowntimeSplit, _f.Stores.Events.Appended[^1].Type);

        // The original is history now; its parts can be split again.
        await Assert.ThrowsAsync<DomainRuleException>(() => SplitAsync(stop, 20));
        await SplitAsync(Stops.Adjustable[split.Stops[1].Id], 20);
        Assert.Equal(4, Stops.Inserted.Count);
    }

    [Fact]
    public async Task Only_a_closed_stop_splits_at_a_time_inside_it()
    {
        var stop = Stop(0, 30);
        var open = Stop(40, null);

        await Assert.ThrowsAsync<DomainRuleException>(() => SplitAsync(stop, 0));
        await Assert.ThrowsAsync<DomainRuleException>(() => SplitAsync(stop, 30));
        await Assert.ThrowsAsync<DomainRuleException>(() => SplitAsync(open, 50));
        await Assert.ThrowsAsync<ValidationException>(() => SplitAsync(stop, 10, comment: null));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            new SplitStopHandler(_f.Stores.UnitOfWork, Stops, _f.Stores.Events, _f.Events, _f.Ids, Sam, _f.Time)
                .HandleAsync(new SplitStopCommand(stop.Id, stop.StartedAt.AddSeconds(1), T0.AddMinutes(10), "x"), Ct));
        Assert.Empty(Stops.Inserted);
    }

    [Fact]
    public async Task A_merge_replaces_consecutive_stops_with_one_spanning_them_with_the_first_ones_reasons()
    {
        var second = Stop(12, 20);
        var first = Stop(0, 10, operatorReason: _jam);

        var merged = await MergeAsync(second, first);

        var stop = Assert.Single(Stops.Inserted);
        Assert.Equal((T0, T0.AddMinutes(20), StopSources.Merge, (Guid?)_jam), (stop.StartedAt, stop.EndedAt, stop.Source, stop.OperatorReasonCodeId));
        Assert.Equal([first.Id, second.Id], stop.ReplacesIds!);
        Assert.True(Stops.Adjustable[first.Id].IsSuperseded && Stops.Adjustable[second.Id].IsSuperseded);
        Assert.Equal(stop.Key, Assert.Single(merged.Stops));
        Assert.Equal(EventTypes.DowntimeMerged, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Only_consecutive_closed_stops_of_one_machine_merge()
    {
        var a = Stop(0, 10);
        var between = Stop(15, 18);
        var b = Stop(20, 25);
        var open = Stop(30, null);
        var elsewhere = Guid.CreateVersion7();
        Stops.AddEquipment(new StopEquipment(elsewhere, "PACK-02", TracksState: true));
        var other = Stop(26, 28, machine: elsewhere);

        await Assert.ThrowsAsync<DomainRuleException>(() => MergeAsync(a, b));
        await Assert.ThrowsAsync<DomainRuleException>(() => MergeAsync(b, open));
        await Assert.ThrowsAsync<DomainRuleException>(() => MergeAsync(b, other));
        await Assert.ThrowsAsync<ValidationException>(() => MergeAsync(a, a));
        Assert.Empty(Stops.Inserted);

        await MergeAsync(a, between, b);
        Assert.Equal((T0, T0.AddMinutes(25)), (Stops.Inserted[0].StartedAt, Stops.Inserted[0].EndedAt));
    }

    [Fact]
    public async Task A_superseded_stop_isnt_coded()
    {
        var machine = Guid.CreateVersion7();
        var stop = Guid.CreateVersion7();
        Stops.AddStop(new StopRecord(stop, T0, T0.AddMinutes(10), machine, "PACK-01", _jam, null, false, null, IsSuperseded: true));
        Stops.AddReason(machine, new ReasonCodeDto { Id = _jam, Kind = "utilization", Code = "JAM", Name = "Jam", UtilizationClass = UtilizationClasses.Downtime });

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            new CodeDowntimeHandler(_f.Stores.UnitOfWork, Stops, _f.Stores.Events, _f.Events, Sam, _f.Time).HandleAsync(new CodeDowntimeCommand(stop, T0, _jam, null), Ct));
    }

    [Fact]
    public void Overlaps_are_half_open_and_an_open_stop_lasts_for_ever()
    {
        StopInterval closed = new(Guid.CreateVersion7(), T0, T0.AddMinutes(10));
        StopInterval open = new(Guid.CreateVersion7(), T0.AddMinutes(30), null);

        Assert.Null(StopAdjustments.FirstOverlap(T0.AddMinutes(10), T0.AddMinutes(30), [closed, open]));
        Assert.Equal(closed, StopAdjustments.FirstOverlap(T0.AddMinutes(9), T0.AddMinutes(11), [open, closed]));
        Assert.Equal(open, StopAdjustments.FirstOverlap(T0.AddDays(5), T0.AddDays(6), [closed, open]));
    }
}
