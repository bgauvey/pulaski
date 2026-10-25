namespace Pulaski.Contracts.MachineData;

/// <summary>A piece of equipment on the machine status board (D-050): its live state, or its state derived from its machines.</summary>
public sealed record MachineStatusDto
{
    public required Guid EquipmentId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public Guid? ParentId { get; init; }
    public required string EquipmentRole { get; init; }

    /// <summary><c>own_tags</c>, <c>series</c>, <c>parallel</c> or <c>constraint_machine</c>.</summary>
    public string StateDerivation { get; init; } = "own_tags";

    /// <summary>The OEE category of its state (D-005): <c>producing</c>, <c>waiting_external</c>, <c>held_internal</c>,
    /// <c>stopped_faulted</c>, <c>ready_transitioning</c> or <c>no_signal</c>; null when nothing is known yet.</summary>
    public string? Category { get; init; }

    /// <summary>True when the state is its machines' (series, parallel or a constraint machine), not its own.</summary>
    public bool IsDerived { get; init; }
    public int? PackMlState { get; init; }
    public string? PackMlName { get; init; }
    public int? RawState { get; init; }
    public int? UnitMode { get; init; }
    public DateTimeOffset? StateSince { get; init; }
    public int? StopCode { get; init; }
    public string? StopCodeText { get; init; }
    public decimal? MachineSpeed { get; init; }
    public DateTimeOffset? SignalAt { get; init; }

    /// <summary>The stop in progress, if any.</summary>
    public MachineOpenStopDto? OpenStop { get; init; }

    /// <summary>The job running on it, and the good machine count of the run so far.</summary>
    public string? JobNumber { get; init; }
    public string? MaterialCode { get; init; }
    public decimal? RunGoodCount { get; init; }

    /// <summary>The edge node it is read through, and whether that node has been quiet for over 2 minutes.</summary>
    public string? EdgeNodeCode { get; init; }
    public bool EdgeQuiet { get; init; }

    /// <summary>Stops of the last 24 hours still to be coded.</summary>
    public int UncodedStops { get; init; }
}

public sealed record MachineOpenStopDto
{
    public required Guid Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string? ReasonCode { get; init; }
    public string? ReasonName { get; init; }
    public bool IsCoded { get; init; }
}

/// <summary>
/// One page of stops, newest first (#10). <see cref="Next"/> continues the list where this page ends; null when there is no more.
/// Pages never skip or repeat a stop, even when several started at the same moment.
/// </summary>
public sealed record StopPageDto
{
    public IReadOnlyList<DowntimeDto> Stops { get; init; } = [];
    public string? Next { get; init; }
}

/// <summary>One stop of a machine (D-050), with the reason its stop code maps to and the one an operator gave.</summary>
public sealed record DowntimeDto
{
    public required Guid Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Seconds stopped so far, for an open stop.</summary>
    public int DurationSeconds { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public int PackMlState { get; init; }
    public required string PackMlName { get; init; }
    public int UnitMode { get; init; }
    public int? StopCode { get; init; }
    public string? StopCodeText { get; init; }
    public Guid? MappedReasonId { get; init; }
    public string? MappedReasonCode { get; init; }
    public string? MappedReasonName { get; init; }
    public Guid? OperatorReasonId { get; init; }
    public string? OperatorReasonCode { get; init; }
    public string? OperatorReasonName { get; init; }

    /// <summary>The effective reason's class: <c>runtime</c>, <c>downtime</c> or <c>planned</c>; null while uncoded.</summary>
    public string? UtilizationClass { get; init; }
    public bool IsMicroStop { get; init; }
    public string? CodedBy { get; init; }
    public DateTimeOffset? CodedAt { get; init; }
    public string? Comment { get; init; }
    public string? JobNumber { get; init; }

    /// <summary>Where it came from (D-069): <c>machine</c>, <c>manual</c>, <c>split</c> or <c>merge</c>.</summary>
    public string Source { get; init; } = "machine";

    /// <summary>A split or merge: the stops it replaced.</summary>
    public IReadOnlyList<Guid> ReplacesIds { get; init; } = [];
    public string? AdjustedBy { get; init; }
    public DateTimeOffset? AdjustedAt { get; init; }
    public string? AdjustmentComment { get; init; }

    /// <summary>When it was replaced by a split or merge; such a stop is history, not counted.</summary>
    public DateTimeOffset? SupersededAt { get; init; }
    public string? SupersededBy { get; init; }
}

/// <summary>Codes a stop: the operator's reason, kept beside the one its stop code maps to (D-005).</summary>
public sealed record CodeDowntimeRequest
{
    /// <summary>The stop's start, which locates it in history.</summary>
    public DateTimeOffset StartedAt { get; init; }
    public Guid ReasonCodeId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>The stop coded.</summary>
public sealed record DowntimeCodedResponse(Guid Id);

/// <summary>A stop entered by hand (D-069), for time the machine didn't report: closed, with its reason and why it was entered.</summary>
public sealed record EnterStopRequest
{
    public Guid EquipmentId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public Guid ReasonCodeId { get; init; }

    /// <summary>Why it was entered; required.</summary>
    public string? Comment { get; init; }
}

/// <summary>Splits a closed stop in two at a time inside it (D-069). Both parts keep its reasons, to be coded on their own.</summary>
public sealed record SplitStopRequest
{
    /// <summary>The stop's start, which locates it in history.</summary>
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset At { get; init; }

    /// <summary>Why; required.</summary>
    public string? Comment { get; init; }
}

/// <summary>A stop, by its id and start, which locates it in history.</summary>
public sealed record StopKeyDto(Guid Id, DateTimeOffset StartedAt);

/// <summary>Merges consecutive closed stops of one machine into one (D-069), with the first one's reasons.</summary>
public sealed record MergeStopsRequest
{
    public IReadOnlyList<StopKeyDto> Stops { get; init; } = [];

    /// <summary>Why; required.</summary>
    public string? Comment { get; init; }
}

/// <summary>The stops an adjustment made, in time order.</summary>
public sealed record StopsAdjustedResponse(IReadOnlyList<StopKeyDto> Stops);

/// <summary>An alarm or warning a machine raised (#26): from a code-valued tag, with the message its machine profile gives the number.</summary>
public sealed record AlarmDto
{
    public required Guid Id { get; init; }
    public DateTimeOffset RaisedAt { get; init; }

    /// <summary>Null while it is still active.</summary>
    public DateTimeOffset? ClearedAt { get; init; }

    /// <summary>How long it was (or has been) active, in seconds.</summary>
    public int DurationSeconds { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }

    /// <summary><c>alarm</c> or <c>warning</c>.</summary>
    public required string Kind { get; init; }
    public int Number { get; init; }
    public string? Message { get; init; }

    /// <summary>The tag that raised it, by its address.</summary>
    public string? Address { get; init; }
}

/// <summary>Alarms and warnings (#26), active first, then newest first. <see cref="IsTruncated"/>: there were more; narrow the period or equipment.</summary>
public sealed record AlarmListDto
{
    public IReadOnlyList<AlarmDto> Alarms { get; init; } = [];
    public bool IsTruncated { get; init; }
}

/// <summary>A process value tag's readings between two times (#26), oldest first, in scaled units.</summary>
public sealed record ProcessValueSeriesDto
{
    public required Guid TagId { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public required string Address { get; init; }
    public string? UomCode { get; init; }
    public IReadOnlyList<ProcessValuePointDto> Points { get; init; } = [];

    /// <summary>True when there were more readings than one request returns: the points stop at the limit; ask again from the last one.</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>One reading: <see cref="Value"/> is null when the source had none (<see cref="Good"/> false).</summary>
public sealed record ProcessValuePointDto(DateTimeOffset At, double? Value, bool Good);
