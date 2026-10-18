namespace Pulaski.Contracts.MachineData;

/// <summary>A piece of equipment on the machine status board (D-041): its live state, or its state derived from its machines.</summary>
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

/// <summary>One stop of a machine (D-041), with the reason its stop code maps to and the one an operator gave.</summary>
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
