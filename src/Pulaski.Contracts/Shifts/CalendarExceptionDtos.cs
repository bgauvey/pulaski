namespace Pulaski.Contracts.Shifts;

/// <summary>
/// Time taken out of the calendar (D-043): a holiday or shutdown on a site, area or line and everything below it, whatever
/// shift schedule it follows. A removed one (<see cref="IsActive"/> false) no longer takes any time out.
/// </summary>
public sealed record CalendarExceptionDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required Guid ScopeEquipmentId { get; init; }
    public string ScopeCode { get; init; } = "";
    public string ScopeName { get; init; } = "";
    public string ScopeLevel { get; init; } = "";
    public required string Kind { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public string? Note { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

public sealed record CreateCalendarExceptionRequest
{
    /// <summary>The site, area or line it applies to, with everything below it.</summary>
    public Guid ScopeEquipmentId { get; init; }
    public string Kind { get; init; } = "holiday";
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public string? Note { get; init; }
}

/// <summary>Changes an exception's kind, times or note; <see cref="IsActive"/> false removes it, true puts it back.</summary>
public sealed record UpdateCalendarExceptionRequest
{
    public long Version { get; init; }
    public string Kind { get; init; } = "holiday";
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public string? Note { get; init; }
    public bool IsActive { get; init; } = true;
}
