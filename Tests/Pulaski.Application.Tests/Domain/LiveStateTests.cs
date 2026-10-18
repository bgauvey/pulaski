using Pulaski.Domain.MachineData;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Signals into live state, counters into counts, and idempotent edge streams (D-041).</summary>
public class LiveStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_state_starts_the_history_and_a_new_meaning_changes_it()
    {
        var (live, first) = LiveStateRules.ApplyState(null, 3, 6, signalLost: false, T0);
        Assert.Equal((3, 6, 1, T0), (live.RawState, live.PackMlState, live.UnitMode, live.StateSince));
        Assert.Null(first!.PrevPackMlState);

        // Another value with the same meaning is kept, but isn't a change.
        var (same, none) = LiveStateRules.ApplyState(live, 4, 6, false, T0.AddMinutes(1));
        Assert.Null(none);
        Assert.Equal((4, T0, T0.AddMinutes(1)), (same.RawState, same.StateSince, same.SignalAt));

        var (stopped, change) = LiveStateRules.ApplyState(same, 1, 2, false, T0.AddMinutes(5));
        Assert.Equal((2, (int?)6, (DateTimeOffset?)T0), (change!.PackMlState, change.PrevPackMlState, change.PrevStartedAt));
        Assert.Equal(T0.AddMinutes(5), stopped.StateSince);
    }

    [Fact]
    public void Losing_the_signal_is_Undefined_with_no_raw_value()
    {
        var (live, _) = LiveStateRules.ApplyState(null, 6, 6, false, T0);
        var (lost, change) = LiveStateRules.ApplyState(live, null, 0, signalLost: true, T0.AddMinutes(2));

        Assert.Equal((PackMlStates.Undefined, (int?)null, true), (lost.PackMlState, lost.RawState, change!.SignalLost));
    }

    [Fact]
    public void A_stop_code_is_latched_until_the_machine_runs_again()
    {
        var (running, _) = LiveStateRules.ApplyState(null, 6, 6, false, T0);
        var (stopped, _) = LiveStateRules.ApplyState(running, 2, 2, false, T0.AddMinutes(1));
        var coded = LiveStateRules.ApplyStopReason(stopped, 101, T0.AddMinutes(1));

        // Acknowledged on the machine: it reports 0, Pulaski keeps 101.
        var acknowledged = LiveStateRules.ApplyStopReason(coded, 0, T0.AddMinutes(2));
        Assert.Equal(101, acknowledged.StopReason);

        var (held, toHeld) = LiveStateRules.ApplyState(acknowledged, 11, 11, false, T0.AddMinutes(3));
        Assert.Equal((101, (int?)101), (held.StopReason, toHeld!.StopReason));

        var (again, _) = LiveStateRules.ApplyState(held, 6, 6, false, T0.AddMinutes(4));
        Assert.Null(again.StopReason);
    }

    [Fact]
    public void A_new_unit_mode_is_a_change_of_its_own()
    {
        var (live, _) = LiveStateRules.ApplyState(null, 6, 6, false, T0);
        var (maintenance, change) = LiveStateRules.ApplyUnitMode(live, 2, T0.AddMinutes(1));

        Assert.Equal((2, 6, (int?)1), (maintenance.UnitMode, change!.PackMlState, change.PrevUnitMode));
        Assert.Null(LiveStateRules.ApplyUnitMode(maintenance, 2, T0.AddMinutes(2)).Change);
    }

    [Theory]
    [InlineData(null, 500L, null, 0L)]
    [InlineData(500L, 530L, null, 30L)]
    [InlineData(500L, 500L, null, 0L)]
    [InlineData(65530L, 4L, 65536L, 10L)]
    [InlineData(800L, 12L, null, 12L)]
    public void A_counter_reading_becomes_a_count(long? previous, long current, long? rollover, long expected) =>
        Assert.Equal(expected, Counters.Delta(previous, current, rollover));

    [Fact]
    public void A_stream_applies_each_sequence_once_and_a_new_stream_starts_again()
    {
        var stream = Guid.CreateVersion7();
        Assert.True(EdgeStreams.IsNew(null, 0, stream, 1));
        Assert.False(EdgeStreams.IsNew(stream, 10, stream, 10));
        Assert.True(EdgeStreams.IsNew(stream, 10, stream, 11));
        Assert.True(EdgeStreams.IsNew(stream, 10, Guid.CreateVersion7(), 1));
    }

    [Fact]
    public void Only_counters_have_a_count_kind()
    {
        Assert.Equal("processed", TagRoles.CountKind(TagRoles.ProcessedCount));
        Assert.Null(TagRoles.CountKind(TagRoles.State));
        Assert.DoesNotContain(TagRoles.Alarm, TagRoles.Recorded);
    }
}
