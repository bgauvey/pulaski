namespace Pulaski.Domain.MachineData;

/// <summary>What a bound tag carries (D-005, D-050): values of <c>core.equipment_tag.tag_role</c>.</summary>
public static class TagRoles
{
    public const string State = "state";
    public const string UnitMode = "unit_mode";
    public const string StopReason = "stop_reason";
    public const string Alarm = "alarm";
    public const string Warning = "warning";
    public const string ProcessedCount = "processed_count";
    public const string DefectiveCount = "defective_count";
    public const string ConsumedCount = "consumed_count";
    public const string MachineSpeed = "machine_speed";
    public const string ProcessValue = "process_value";

    public static IReadOnlyList<string> All { get; } =
        [State, UnitMode, StopReason, Alarm, Warning, ProcessedCount, DefectiveCount, ConsumedCount, MachineSpeed, ProcessValue];

    /// <summary>The roles Pulaski records today; alarms, warnings and process values come later.</summary>
    public static IReadOnlyList<string> Recorded { get; } = [State, UnitMode, StopReason, ProcessedCount, DefectiveCount, ConsumedCount, MachineSpeed];

    /// <summary>The <c>ts.production_count.count_kind</c> a counter role feeds, or null when it isn't a counter.</summary>
    public static string? CountKind(string role) => role switch
    {
        ProcessedCount => "processed",
        DefectiveCount => "defective",
        ConsumedCount => "consumed",
        _ => null,
    };
}

/// <summary>A machine's state as its signals last left it (D-050), as <c>core.equipment_live_state</c> holds it.</summary>
public sealed record MachineLive(
    int? RawState, int PackMlState, int UnitMode, DateTimeOffset StateSince, int? StopReason, decimal? MachineSpeed, DateTimeOffset SignalAt);

/// <summary>A change of state or unit mode, for <c>ts.equipment_state_change</c>.</summary>
public sealed record StateChange(
    DateTimeOffset OccurredAt, int? RawState, int PackMlState, int UnitMode, int? PrevPackMlState, int? PrevUnitMode, DateTimeOffset? PrevStartedAt,
    int? StopReason, bool SignalLost);

/// <summary>
/// How signals change a machine's live state (D-050). Signals arrive in the order the edge read them. The state's raw value is
/// stored with its PackML meaning; losing the signal is PackML 0, Undefined (D-005). The first-out stop code is latched: a
/// machine that clears it once acknowledged doesn't lose it, and it is cleared only when the machine runs (Execute) again.
/// </summary>
public static class LiveStateRules
{
    public const int Execute = 6;

    /// <summary>The unit mode a machine is taken to be in until it reports one: Production.</summary>
    public const int DefaultUnitMode = 1;

    public static (MachineLive Live, StateChange? Change) ApplyState(MachineLive? live, int? rawState, int packMlState, bool signalLost, DateTimeOffset at)
    {
        var state = signalLost ? PackMlStates.Undefined : packMlState;
        var raw = signalLost ? null : rawState;
        if (live is null)
        {
            var first = new MachineLive(raw, state, DefaultUnitMode, at, null, null, at);
            return (first, new StateChange(at, raw, state, DefaultUnitMode, null, null, null, null, signalLost));
        }

        var signalAt = Max(live.SignalAt, at);
        if (live.PackMlState == state)
        {
            // The same meaning: a new raw value (two values meaning Stopped, say) is kept without a change of state.
            return (live with { RawState = raw ?? live.RawState, SignalAt = signalAt }, null);
        }

        var stopReason = state == Execute ? null : live.StopReason;
        var next = live with { RawState = raw, PackMlState = state, StateSince = at, StopReason = stopReason, SignalAt = signalAt };
        return (next, new StateChange(at, raw, state, live.UnitMode, live.PackMlState, live.UnitMode, live.StateSince, live.StopReason, signalLost));
    }

    public static (MachineLive Live, StateChange? Change) ApplyUnitMode(MachineLive? live, int unitMode, DateTimeOffset at)
    {
        if (live is null)
        {
            // No state yet: the mode waits for one, Undefined until the machine says.
            var first = new MachineLive(null, PackMlStates.Undefined, unitMode, at, null, null, at);
            return (first, new StateChange(at, null, PackMlStates.Undefined, unitMode, null, null, null, null, SignalLost: false));
        }

        var signalAt = Max(live.SignalAt, at);
        if (live.UnitMode == unitMode)
        {
            return (live with { SignalAt = signalAt }, null);
        }

        var next = live with { UnitMode = unitMode, StateSince = at, SignalAt = signalAt };
        return (next, new StateChange(at, live.RawState, live.PackMlState, unitMode, live.PackMlState, live.UnitMode, live.StateSince, live.StopReason, SignalLost: false));
    }

    /// <summary>A new first-out stop code; 0 (none, or acknowledged) keeps the one latched.</summary>
    public static MachineLive ApplyStopReason(MachineLive? live, int stopReason, DateTimeOffset at)
    {
        live ??= new MachineLive(null, PackMlStates.Undefined, DefaultUnitMode, at, null, null, at);
        return live with { StopReason = stopReason == 0 ? live.StopReason : stopReason, SignalAt = Max(live.SignalAt, at) };
    }

    public static MachineLive ApplySpeed(MachineLive? live, decimal speed, DateTimeOffset at)
    {
        live ??= new MachineLive(null, PackMlStates.Undefined, DefaultUnitMode, at, null, null, at);
        return live with { MachineSpeed = speed, SignalAt = Max(live.SignalAt, at) };
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}

/// <summary>Machine counters (PackML <c>Admin.ProdProcessedCount</c> and the like) turned into counts (D-050).</summary>
public static class Counters
{
    /// <summary>
    /// The count between two readings: the difference; across the rollover value when the counter wraps; or, when it went
    /// back without a rollover set, the new reading (the counter was reset to zero). The first reading only sets the baseline.
    /// </summary>
    public static long Delta(long? previous, long current, long? rollover)
    {
        if (previous is not { } last)
        {
            return 0;
        }

        if (current >= last)
        {
            return current - last;
        }

        return rollover is { } wrap && wrap > last ? wrap - last + current : current;
    }
}

/// <summary>Idempotent forwarding from an edge node (D-050): each batch carries its buffer's stream id and sequence numbers.</summary>
public static class EdgeStreams
{
    /// <summary>Whether a signal with <paramref name="sequence"/> of <paramref name="stream"/> is still to be applied.</summary>
    public static bool IsNew(Guid? knownStream, long lastSequence, Guid stream, long sequence) =>
        knownStream != stream || sequence > lastSequence;
}
