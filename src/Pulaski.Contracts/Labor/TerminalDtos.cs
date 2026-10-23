namespace Pulaski.Contracts.Labor;

// ---- Managing terminals and badges (D-049) ----

/// <summary>A shop-floor terminal: where it is, which lines it offers, and whether it is enrolled.</summary>
public sealed record TerminalDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public Guid ScopeEquipmentId { get; init; }
    public string ScopeCode { get; init; } = "";
    public Guid? DefaultEquipmentId { get; init; }
    public string? DefaultCode { get; init; }

    /// <summary><c>badge</c> or <c>badge_plus_pin</c>.</summary>
    public string AuthMode { get; init; } = "badge";
    public bool IsActive { get; init; }
    public DateTimeOffset? EnrolledAt { get; init; }

    /// <summary>When a one-time enrolment code is waiting, until when it can be used.</summary>
    public DateTimeOffset? EnrolmentExpiresAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public long Version { get; init; }
}

public sealed record CreateTerminalRequest
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>The site, area or line whose lines it offers.</summary>
    public Guid ScopeEquipmentId { get; init; }
    public Guid? DefaultEquipmentId { get; init; }
    public string AuthMode { get; init; } = "badge";
}

public sealed record UpdateTerminalRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public Guid ScopeEquipmentId { get; init; }
    public Guid? DefaultEquipmentId { get; init; }
    public string AuthMode { get; init; } = "badge";
    public bool IsActive { get; init; } = true;
}

/// <summary>A one-time code to type on the terminal, shown once; it replaces any earlier one.</summary>
public sealed record EnrolmentCodeResponse(string Code, DateTimeOffset ExpiresAt);

/// <summary>A person's badge, without its value (only a hash is kept).</summary>
public sealed record BadgeDto
{
    public required Guid Id { get; init; }
    public required Guid PersonId { get; init; }
    public string PersonName { get; init; } = "";
    public string EmployeeNumber { get; init; } = "";
    public bool HasPin { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
    public string IssuedBy { get; init; } = "";
    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokedBy { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>Issues a badge: its value as the reader gives it, and a PIN for terminals that ask for one. It replaces the person's badge.</summary>
public sealed record IssueBadgeRequest
{
    public Guid PersonId { get; init; }
    public string BadgeValue { get; init; } = "";
    public string? Pin { get; init; }
}

public sealed record RevokeBadgeRequest
{
    public string? Reason { get; init; }
}

// ---- At the terminal ----

/// <summary>Enrols a terminal with the one-time code an administrator issued.</summary>
public sealed record EnrolTerminalRequest
{
    public string Code { get; init; } = "";
}

/// <summary>The terminal's secret, given once: keep it, it is the terminal's credential.</summary>
public sealed record TerminalEnrolledResponse(Guid TerminalId, string Code, string Name, string Secret);

/// <summary>A badge scanned at the terminal, with a PIN when it asks for one.</summary>
public sealed record IdentifyBadgeRequest
{
    public string BadgeValue { get; init; } = "";
    public string? Pin { get; init; }
}

/// <summary>
/// Who a badge belongs to (D-049). With <see cref="NeedsPin"/> the terminal asks for the PIN and nothing else is said. Otherwise
/// <see cref="Ticket"/> identifies the person on the next action at this terminal, for a short while.
/// </summary>
public sealed record BadgeIdentityResponse
{
    public bool NeedsPin { get; init; }
    public string? Ticket { get; init; }
    public DateTimeOffset? TicketExpiresAt { get; init; }
    public TerminalStatusDto? Status { get; init; }
}

/// <summary>A line the terminal offers.</summary>
public sealed record TerminalLineDto(Guid Id, string Code, string Name, string? RunningJobNumber);

/// <summary>What the terminal shows: itself, its lines, and (once someone has badged) that person's time.</summary>
public sealed record TerminalStatusDto
{
    public required Guid TerminalId { get; init; }
    public required string TerminalCode { get; init; }
    public required string TerminalName { get; init; }
    public string AuthMode { get; init; } = "badge";
    public string? TimeZone { get; init; }
    public Guid? DefaultEquipmentId { get; init; }
    public IReadOnlyList<TerminalLineDto> Lines { get; init; } = [];
    public Guid? PersonId { get; init; }
    public string? PersonName { get; init; }
    public IReadOnlyList<LaborSessionDto> Sessions { get; init; } = [];
    public BreakDto? OpenBreak { get; init; }
}

/// <summary>An action at the terminal for the person a badge identified.</summary>
public sealed record TerminalActionRequest
{
    public string Ticket { get; init; } = "";
    public Guid? EquipmentId { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? BreakId { get; init; }
    public bool IsPaid { get; init; }
}
