namespace Pulaski.Domain.MachineData;

/// <summary>
/// What a PackML state counts as for OEE (D-005): values of <c>core.oee_state_category.category</c>. A site may map states
/// otherwise; this is the default. Ordered from worst to best, which is how a line's state is taken from its machines (D-050).
/// </summary>
public static class OeeCategories
{
    public const string NoSignal = "no_signal";
    public const string StoppedFaulted = "stopped_faulted";
    public const string HeldInternal = "held_internal";
    public const string WaitingExternal = "waiting_external";
    public const string ReadyTransitioning = "ready_transitioning";
    public const string Producing = "producing";

    /// <summary>Worst first.</summary>
    public static IReadOnlyList<string> BySeverity { get; } = [NoSignal, StoppedFaulted, HeldInternal, WaitingExternal, ReadyTransitioning, Producing];

    /// <summary>The default category of a PackML state (D-005).</summary>
    public static string Default(int packMlState) => packMlState switch
    {
        6 => Producing,
        5 or 13 or 14 => WaitingExternal,
        10 or 11 or 12 => HeldInternal,
        1 or 2 or 7 or 8 or 9 => StoppedFaulted,
        PackMlStates.Undefined => NoSignal,
        _ => ReadyTransitioning,
    };

    /// <summary>0 for the worst (no signal) to 5 for producing.</summary>
    public static int Rank(string category) => BySeverity.ToList().IndexOf(category) is var i and >= 0 ? i : 0;
}

/// <summary>How a line's state comes from its machines (D-050).</summary>
public static class LineStates
{
    /// <summary>
    /// The category of a line deriving its state from its machines' categories: the worst in series, the best in parallel.
    /// With no machine reporting, no signal. Own tags and constraint machine aren't derived from several, so they return null.
    /// </summary>
    public static string? Derive(string derivation, IReadOnlyCollection<string> machineCategories) =>
        derivation switch
        {
            StateDerivations.Series => machineCategories.Count == 0 ? OeeCategories.NoSignal : machineCategories.MinBy(OeeCategories.Rank),
            StateDerivations.Parallel => machineCategories.Count == 0 ? OeeCategories.NoSignal : machineCategories.MaxBy(OeeCategories.Rank),
            _ => null,
        };
}

/// <summary>An open or just-closed stop of a machine, as <c>ts.downtime</c> holds it.</summary>
public sealed record MachineStop(
    Guid Id, Guid EquipmentId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, Guid? RunId, int PackMlState, int UnitMode,
    int? PlcStopReason, Guid? MappedReasonCodeId, bool IsMicroStop)
{
    public TimeSpan? Duration => EndedAt - StartedAt;
}

/// <summary>What a machine's profile says about its stops: its stop codes' reasons (with this machine's overrides) and each state's reason.</summary>
public sealed record StopReasons(IReadOnlyDictionary<int, Guid?> ByStopCode, IReadOnlyDictionary<int, Guid> ByPackMlState)
{
    public static StopReasons None { get; } = new(new Dictionary<int, Guid?>(), new Dictionary<int, Guid>());

    /// <summary>The reason a stop is mapped to: its first-out code's, or with no code (or none mapped), its first state's.</summary>
    public Guid? For(int? stopCode, int packMlState) =>
        stopCode is { } code && ByStopCode.TryGetValue(code, out var byCode)
            ? byCode
            : ByPackMlState.TryGetValue(packMlState, out var byState) ? byState : null;
}

/// <summary>
/// Stops from state changes (D-050). A stop is time the machine isn't executing: it starts when the machine leaves Execute (or
/// first reports another state, or loses its signal) and ends when it executes again; the states it passes through on the way
/// stay in the state history. Its reason is mapped from the first-out stop code latched during it, else from the first state on its
/// way that has one. A stop shorter than the machine's threshold is a micro-stop: logged, not to be coded, not availability loss.
/// </summary>
public static class StopRules
{
    /// <returns>The stop open after the change, and the stop the change closed, if any.</returns>
    public static (MachineStop? Open, MachineStop? Closed) Apply(MachineStop? open, StateChange change, Guid equipmentId, Guid? runId, StopReasons reasons, TimeSpan microStop, Func<Guid> newId)
    {
        var executing = change.PackMlState == LiveStateRules.Execute;
        if (open is null)
        {
            return executing
                ? (null, null)
                : (new MachineStop(newId(), equipmentId, change.OccurredAt, null, runId, change.PackMlState, change.UnitMode, change.StopReason,
                    reasons.For(change.StopReason, change.PackMlState), IsMicroStop: false), null);
        }

        if (!executing)
        {
            // Passing through states (Suspending, then Suspended): without a code, the first state on the way that has a reason gives it.
            return open.MappedReasonCodeId is null && open.PlcStopReason is null && reasons.For(null, change.PackMlState) is { } byState
                ? (open with { MappedReasonCodeId = byState }, null)
                : (open, null);
        }

        var ended = change.OccurredAt < open.StartedAt ? open.StartedAt : change.OccurredAt;
        var closed = open with { EndedAt = ended, IsMicroStop = ended - open.StartedAt < microStop };
        return (null, closed);
    }

    /// <summary>A first-out code arriving during a stop (it often comes just after the state): the first one is the stop's.</summary>
    public static MachineStop WithStopCode(MachineStop open, int stopCode, StopReasons reasons) =>
        open.PlcStopReason is not null || stopCode == 0
            ? open
            : open with { PlcStopReason = stopCode, MappedReasonCodeId = reasons.For(stopCode, open.PackMlState) ?? open.MappedReasonCodeId };
}
