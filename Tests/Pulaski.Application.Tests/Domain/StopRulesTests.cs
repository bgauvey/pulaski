using Pulaski.Domain.MachineData;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Stops from state changes, OEE categories and line state (D-005, D-050).</summary>
public class StopRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Machine = Guid.CreateVersion7();
    private static readonly Guid Jam = Guid.CreateVersion7();
    private static readonly Guid Starved = Guid.CreateVersion7();
    private static readonly StopReasons Reasons = new(new Dictionary<int, Guid?> { [101] = Jam, [999] = null }, new Dictionary<int, Guid> { [5] = Starved });

    private static StateChange To(int state, int minutes, int? stopCode = null, int mode = 1) =>
        new(T0.AddMinutes(minutes), state, state, mode, null, null, null, stopCode, SignalLost: state == 0);

    private static (MachineStop? Open, MachineStop? Closed) Apply(MachineStop? open, StateChange change, int microStopMinutes = 5) =>
        StopRules.Apply(open, change, Machine, null, Reasons, TimeSpan.FromMinutes(microStopMinutes), Guid.CreateVersion7);

    [Fact]
    public void Leaving_Execute_opens_a_stop_and_executing_again_closes_it()
    {
        var (open, none) = Apply(null, To(2, 0));
        Assert.Null(none);
        Assert.Equal((T0, 2, (DateTimeOffset?)null), (open!.StartedAt, open.PackMlState, open.EndedAt));

        // Through other states the stop stays the one stop.
        var (still, _) = Apply(open, To(15, 3));
        Assert.Same(open, still);

        var (after, closed) = Apply(still, To(6, 12));
        Assert.Null(after);
        Assert.Equal((T0.AddMinutes(12), false, TimeSpan.FromMinutes(12)), (closed!.EndedAt, closed.IsMicroStop, closed.Duration));
        Assert.Equal((null, null), Apply(null, To(6, 20)));
    }

    [Fact]
    public void A_stop_shorter_than_the_threshold_is_a_micro_stop()
    {
        var (open, _) = Apply(null, To(5, 0));
        Assert.True(Apply(open, To(6, 4)).Closed!.IsMicroStop);
        Assert.False(Apply(open, To(6, 4), microStopMinutes: 2).Closed!.IsMicroStop);
    }

    [Fact]
    public void A_stop_is_mapped_by_its_first_out_code_else_by_the_state_it_stopped_in()
    {
        Assert.Equal(Starved, Apply(null, To(5, 0)).Open!.MappedReasonCodeId);
        Assert.Equal(Jam, Apply(null, To(2, 0, stopCode: 101)).Open!.MappedReasonCodeId);

        // The code often comes just after the state: the first one is the stop's.
        var (open, _) = Apply(null, To(2, 0));
        var coded = StopRules.WithStopCode(open!, 101, Reasons);
        Assert.Equal((101, (Guid?)Jam), (coded.PlcStopReason, coded.MappedReasonCodeId));
        Assert.Same(coded, StopRules.WithStopCode(coded, 207, Reasons));
        Assert.Same(open, StopRules.WithStopCode(open!, 0, Reasons));

        // A machine's override to leave a code uncoded beats the state's reason.
        Assert.Null(Apply(null, To(5, 0, stopCode: 999)).Open!.MappedReasonCodeId);
    }

    [Fact]
    public void Without_a_code_the_first_state_on_the_way_with_a_reason_gives_it()
    {
        var (suspending, _) = Apply(null, To(13, 0));
        Assert.Null(suspending!.MappedReasonCodeId);

        var (suspended, _) = Apply(suspending, To(5, 1));
        Assert.Equal((suspending.Id, (Guid?)Starved), (suspended!.Id, suspended.MappedReasonCodeId));

        // A code wins over any state on the way.
        var (coded, _) = Apply(null, To(2, 0, stopCode: 101));
        Assert.Same(coded, Apply(coded, To(5, 1)).Open);
    }

    [Fact]
    public void Losing_the_signal_is_a_stop_too() => Assert.Equal(0, Apply(null, To(0, 0)).Open!.PackMlState);

    [Theory]
    [InlineData(6, OeeCategories.Producing)]
    [InlineData(5, OeeCategories.WaitingExternal)]
    [InlineData(11, OeeCategories.HeldInternal)]
    [InlineData(9, OeeCategories.StoppedFaulted)]
    [InlineData(4, OeeCategories.ReadyTransitioning)]
    [InlineData(0, OeeCategories.NoSignal)]
    public void Each_PackML_state_has_a_default_category(int state, string category) => Assert.Equal(category, OeeCategories.Default(state));

    [Fact]
    public void A_line_in_series_is_its_worst_machine_and_in_parallel_its_best()
    {
        string[] machines = [OeeCategories.Producing, OeeCategories.WaitingExternal, OeeCategories.Producing];
        Assert.Equal(OeeCategories.WaitingExternal, LineStates.Derive(StateDerivations.Series, machines));
        Assert.Equal(OeeCategories.Producing, LineStates.Derive(StateDerivations.Parallel, machines));
        Assert.Equal(OeeCategories.NoSignal, LineStates.Derive(StateDerivations.Series, []));
        Assert.Null(LineStates.Derive(StateDerivations.OwnTags, machines));
        Assert.Equal(OeeCategories.NoSignal, LineStates.Derive(StateDerivations.Parallel, [OeeCategories.NoSignal, OeeCategories.NoSignal]));
    }
}
