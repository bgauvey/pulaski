namespace Pulaski.Contracts.Maintenance;

/// <summary>A request for maintenance (#127, D-115), with how long it has waited, worked out when read.</summary>
public sealed record MaintenanceRequestDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Number { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public string? EquipmentName { get; init; }

    /// <summary><c>open</c>, <c>in_progress</c> or <c>closed</c>.</summary>
    public required string Status { get; init; }

    /// <summary><c>low</c>, <c>normal</c> or <c>high</c>.</summary>
    public required string Priority { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
    public required string RaisedBy { get; init; }

    /// <summary>The operator station's code, when raised there.</summary>
    public string? RaisedFrom { get; init; }

    /// <summary>The stop it was raised from, if any: its id and start (the stop's key), and how long it lasted (null while it goes on).</summary>
    public Guid? DowntimeId { get; init; }
    public DateTimeOffset? DowntimeStartedAt { get; init; }
    public DateTimeOffset? DowntimeEndedAt { get; init; }
    public string? DowntimeReason { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public string? StartedBy { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? ClosedBy { get; init; }
    public string? Resolution { get; init; }

    /// <summary>The users' names for <see cref="RaisedBy"/>, <see cref="StartedBy"/> and <see cref="ClosedBy"/>, when they are users.</summary>
    public string? RaisedByName { get; init; }
    public string? StartedByName { get; init; }
    public string? ClosedByName { get; init; }

    /// <summary>How long it waited for someone to start: to its start, or to now while open.</summary>
    public int WaitedSeconds { get; init; }

    /// <summary>How long it has been open altogether: to its close, or to now.</summary>
    public int AgeSeconds { get; init; }
}

public sealed record RaiseMaintenanceRequestRequest
{
    /// <summary>The line or machine; with a stop, the stop's equipment is used.</summary>
    public Guid EquipmentId { get; init; }

    /// <summary>The stop it is raised from: its id and start.</summary>
    public Guid? DowntimeId { get; init; }
    public DateTimeOffset? DowntimeStartedAt { get; init; }
    public string? Priority { get; init; }
    public string Description { get; init; } = "";
}

public sealed record CloseMaintenanceRequestRequest
{
    public string Resolution { get; init; } = "";
}

/// <summary>What raising, starting or closing did. <see cref="AlreadyOpen"/>: the stop already had a request still open, which is returned.</summary>
public sealed record MaintenanceRequestResponse(Guid Id, string Number, string Status, bool AlreadyOpen = false);
