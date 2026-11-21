namespace Pulaski.Contracts.Alerts;

/// <summary>The signed-in user's e-mail alerts (#121): whether they can arrive, the kinds offered and their subscriptions.</summary>
public sealed record MyAlertsDto
{
    /// <summary>Whether a mail server is configured; without one nothing is sent.</summary>
    public bool CanSend { get; init; }

    /// <summary>Where alerts go: the user's e-mail address; null when they have none.</summary>
    public string? Email { get; init; }

    public IReadOnlyList<AlertKindDto> Kinds { get; init; } = [];
    public IReadOnlyList<AlertSubscriptionDto> Subscriptions { get; init; } = [];
}

/// <summary>A kind of alert offered: what it says, and what its minutes mean (null when it takes none).</summary>
public sealed record AlertKindDto(string Code, string Name, string? MinutesMeans, int? DefaultMinutes);

public sealed record AlertSubscriptionDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string SiteCode { get; init; }

    /// <summary>The line or area it is narrowed to; null for the whole site.</summary>
    public Guid? EquipmentId { get; init; }
    public string? EquipmentCode { get; init; }
    public required string Kind { get; init; }
    public int? Minutes { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Whether the user may see what it is about at the site now; alerts they may not see aren't sent.</summary>
    public bool CanSee { get; init; }
    public long Version { get; init; }
}

public sealed record CreateAlertSubscriptionRequest
{
    public Guid SiteId { get; init; }
    public Guid? EquipmentId { get; init; }
    public string Kind { get; init; } = "";
    public int? Minutes { get; init; }
}

public sealed record UpdateAlertSubscriptionRequest
{
    public long Version { get; init; }
    public int? Minutes { get; init; }
    public bool IsActive { get; init; }
}
