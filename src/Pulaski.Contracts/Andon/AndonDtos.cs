namespace Pulaski.Contracts.Andon;

/// <summary>A kind of help a line can call for at a site (#125, D-108).</summary>
public sealed record AndonTypeDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary>A call still waiting this long is escalated.</summary>
    public int EscalateAfterMinutes { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

/// <summary>A call for help from a line (#125, D-108), with how long it waited and whether it is escalated, worked out when read.</summary>
public sealed record AndonCallDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required Guid LineId { get; init; }
    public required string LineCode { get; init; }
    public string? LineName { get; init; }
    public required Guid TypeId { get; init; }
    public required string TypeCode { get; init; }
    public required string TypeName { get; init; }
    public int EscalateAfterMinutes { get; init; }

    /// <summary><c>open</c>, <c>acknowledged</c> or <c>resolved</c>.</summary>
    public required string Status { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
    public required string RaisedBy { get; init; }

    /// <summary>The operator station's code, when raised there.</summary>
    public string? RaisedFrom { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public string? AcknowledgedBy { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }
    public string? Resolution { get; init; }

    /// <summary>The users' names for <see cref="RaisedBy"/>, <see cref="AcknowledgedBy"/> and <see cref="ResolvedBy"/>, when they are users.</summary>
    public string? RaisedByName { get; init; }
    public string? AcknowledgedByName { get; init; }
    public string? ResolvedByName { get; init; }

    /// <summary>Still open past its type's escalation time.</summary>
    public bool IsEscalated { get; init; }

    /// <summary>How long it waited for someone: to its acknowledgment, or to now while open.</summary>
    public int WaitedSeconds { get; init; }
}

public sealed record CreateAndonTypeRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public int EscalateAfterMinutes { get; init; } = 10;
    public int SortOrder { get; init; }
}

public sealed record UpdateAndonTypeRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public int EscalateAfterMinutes { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
}

public sealed record RaiseAndonCallRequest
{
    public Guid LineId { get; init; }
    public Guid TypeId { get; init; }
    public string? Note { get; init; }
}

public sealed record ResolveAndonCallRequest
{
    public string? Resolution { get; init; }
}

/// <summary>A call as it stands after a change, and whether raising it found one already open (the same call).</summary>
public sealed record AndonCallResponse(Guid Id, string Status, bool AlreadyOpen = false);

/// <summary>How fast calls for help were answered over a period (#159): by line and kind of help, and the slowest calls.</summary>
public sealed record AndonResponseTimesDto
{
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }

    /// <summary>One row per line and kind of help with calls in the period, by line then the kind's order; then <see cref="Total"/>.</summary>
    public IReadOnlyList<AndonResponseRowDto> Rows { get; init; } = [];
    public AndonResponseRowDto? Total { get; init; }

    /// <summary>The calls that waited longest for someone, longest first (at most ten).</summary>
    public IReadOnlyList<AndonCallDto> Slowest { get; init; } = [];
}

/// <summary>
/// Calls raised in the period on one line for one kind of help (or all of them, for the total): how many, how many escalated, and the times
/// to answer (raised → acknowledged) and to resolve (raised → resolved), in seconds, over the calls that got there.
/// </summary>
public sealed record AndonResponseRowDto
{
    public Guid? LineId { get; init; }
    public string? LineCode { get; init; }
    public Guid? TypeId { get; init; }
    public string? TypeName { get; init; }
    public int Calls { get; init; }
    public int Escalated { get; init; }

    /// <summary>Not yet resolved at the time of the report.</summary>
    public int StillOpen { get; init; }
    public int? AverageAnswerSeconds { get; init; }
    public int? LongestAnswerSeconds { get; init; }
    public int? AverageResolveSeconds { get; init; }
}
