namespace Pulaski.Domain.Maintenance;

/// <summary>Where a maintenance request stands (#127, D-115). Values match <c>core.maintenance_request.status</c>.</summary>
public static class MaintenanceStatuses
{
    /// <summary>Raised; nobody has started on it.</summary>
    public const string Open = "open";

    /// <summary>Someone is working on it.</summary>
    public const string InProgress = "in_progress";

    /// <summary>Done, with what was done.</summary>
    public const string Closed = "closed";
}

/// <summary>How soon a request needs doing. Values match <c>core.maintenance_request.priority</c>.</summary>
public static class MaintenancePriorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";

    public static IReadOnlyList<string> All { get; } = [Low, Normal, High];
}

/// <summary>
/// A request for maintenance to look at a line or machine (#127, D-115), raised on its own or from one of its stops: open → in progress → closed,
/// with who did each and what was done. Pulaski keeps it whether or not a CMMS also tracks it. Never deleted.
/// </summary>
public sealed record MaintenanceRequest(
    Guid Id,
    Guid SiteId,
    Guid EquipmentId,
    string Number,
    string Status,
    string Priority,
    string Description,
    DateTimeOffset RaisedAt,
    string RaisedBy,
    string? RaisedFrom = null,
    Guid? DowntimeId = null,
    DateTimeOffset? DowntimeStartedAt = null,
    DateTimeOffset? StartedAt = null,
    string? StartedBy = null,
    DateTimeOffset? ClosedAt = null,
    string? ClosedBy = null,
    string? Resolution = null,
    string? ExternalSystem = null,
    string? ExternalReference = null)
{
    public const int MaxText = 1000;

    public const int MaxReference = 100;

    /// <summary>A request's number: <c>MR-000042</c>, from the database's sequence.</summary>
    public static string NumberFor(long sequence) => $"MR-{sequence:D6}";

    /// <exception cref="DomainRuleException">No description, too long a text, or an unknown priority.</exception>
    public static MaintenanceRequest Raise(
        Guid id, Guid siteId, Guid equipmentId, string number, string? priority, string? description, DateTimeOffset at, string by, string? from = null,
        Guid? downtimeId = null, DateTimeOffset? downtimeStartedAt = null)
    {
        var chosen = string.IsNullOrWhiteSpace(priority) ? MaintenancePriorities.Normal : priority.Trim();
        if (!MaintenancePriorities.All.Contains(chosen))
        {
            throw new DomainRuleException($"'{chosen}' isn't a priority; use low, normal or high.");
        }

        var text = Text(description) ?? throw new DomainRuleException("Say what needs looking at.");
        return new MaintenanceRequest(id, siteId, equipmentId, number, MaintenanceStatuses.Open, chosen, text, at, by, from, downtimeId, downtimeStartedAt);
    }

    public bool IsClosed => Status == MaintenanceStatuses.Closed;

    /// <summary>Someone has started on it.</summary>
    /// <exception cref="DomainRuleException">It is already in progress or closed.</exception>
    public MaintenanceRequest Start(DateTimeOffset at, string by) => Status switch
    {
        MaintenanceStatuses.Open => this with { Status = MaintenanceStatuses.InProgress, StartedAt = at, StartedBy = by },
        MaintenanceStatuses.InProgress => throw new DomainRuleException($"{Number} is in progress already."),
        _ => throw new DomainRuleException($"{Number} is closed already."),
    };

    /// <summary>Done, saying what was done. Closing one nobody started starts it at the same moment, by the same person.</summary>
    /// <exception cref="DomainRuleException">It is closed already, or nothing says what was done.</exception>
    public MaintenanceRequest Close(DateTimeOffset at, string by, string? resolution)
    {
        if (IsClosed)
        {
            throw new DomainRuleException($"{Number} is closed already.");
        }

        var text = Text(resolution) ?? throw new DomainRuleException("Say what was done, or why nothing was.");
        return this with
        {
            Status = MaintenanceStatuses.Closed,
            StartedAt = StartedAt ?? at,
            StartedBy = StartedBy ?? by,
            ClosedAt = at,
            ClosedBy = by,
            Resolution = text,
        };
    }

    /// <summary>
    /// A CMMS has taken it on as its own work order (#127c): <paramref name="system"/> is the feed it came through, <paramref name="reference"/> the
    /// CMMS's number. Telling it again with the same number changes nothing; another number replaces it (the CMMS renumbered it).
    /// </summary>
    /// <exception cref="DomainRuleException">No number, or one too long.</exception>
    public MaintenanceRequest LinkExternal(string system, string? reference)
    {
        var trimmed = reference?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxReference)
        {
            throw new DomainRuleException($"Give the CMMS's number for {Number}, at most {MaxReference} characters.");
        }

        return this with { ExternalSystem = system, ExternalReference = trimmed };
    }

    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is { Length: > MaxText })
        {
            throw new DomainRuleException($"Keep it to {MaxText} characters.");
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>How long a request has waited (#127): worked out on read, never stored. Pure.</summary>
public static class MaintenanceRules
{
    /// <summary>How long it waited for someone to start: to its start, or to now while open.</summary>
    public static TimeSpan Waited(MaintenanceRequest request, DateTimeOffset now) => (request.StartedAt ?? now) - request.RaisedAt;

    /// <summary>How long it has been open altogether: to its close, or to now.</summary>
    public static TimeSpan Age(MaintenanceRequest request, DateTimeOffset now) => (request.ClosedAt ?? now) - request.RaisedAt;
}
