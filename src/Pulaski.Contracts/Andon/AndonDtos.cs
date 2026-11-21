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

    /// <summary>Still open past its type's escalation time.</summary>
    public bool IsEscalated { get; init; }

    /// <summary>How long it waited for someone: to its acknowledgement, or to now while open.</summary>
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
