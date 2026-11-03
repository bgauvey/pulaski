namespace Pulaski.Contracts.Integrations;

/// <summary>One event's delivery to a sink (D-061).</summary>
public sealed record DeliveryDto
{
    public required Guid Id { get; init; }
    public required string Sink { get; init; }
    public required Guid EventId { get; init; }
    public required string EventType { get; init; }
    public required Guid Subject { get; init; }
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary><c>pending</c>, <c>delivered</c>, <c>failed</c> or <c>skipped</c>.</summary>
    public required string Status { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset NextAttemptAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public int? LastStatusCode { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public string? ResolvedBy { get; init; }
    public string? ResolveNote { get; init; }

    /// <summary>The receiver's acknowledgement (#73): <c>accepted</c> or <c>rejected</c>, its reference and message, and how it came.</summary>
    public string? AckStatus { get; init; }
    public string? AckReference { get; init; }
    public string? AckMessage { get; init; }
    public string? AckSource { get; init; }
    public DateTimeOffset? AckAt { get; init; }

    /// <summary>True when it waits behind an undelivered earlier event of the same subject.</summary>
    public bool IsHeld { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The subject lot's number, when the subject is a lot that has one.</summary>
    public string? LotNumber { get; init; }

    /// <summary>True when the subject is a balance (D-031).</summary>
    public bool IsBulk { get; init; }

    /// <summary>Where the subject lot is now.</summary>
    public string? LocationCode { get; init; }
}

/// <summary>How the ERP feed is set up and how it is doing (D-061).</summary>
public sealed record DeliveryStatusDto
{
    public required string Sink { get; init; }

    /// <summary>The feed's key: <c>erp</c> or <c>documents</c> (#78), and what it is for.</summary>
    public string Feed { get; init; } = "erp";
    public string Label { get; init; } = "ERP";

    /// <summary>A certificate's stored file goes with its event.</summary>
    public bool IncludesFiles { get; init; }
    public bool Enabled { get; init; }

    /// <summary>The receiver's host only, never the full address or its secret.</summary>
    public string? TargetHost { get; init; }
    public IReadOnlyList<string> EventTypes { get; init; } = [];
    public int Pending { get; init; }
    public int Failed { get; init; }
    public int Held { get; init; }
    public int DeliveredLastDay { get; init; }
    public DateTimeOffset? OldestPendingAt { get; init; }
    public DateTimeOffset? LastDeliveredAt { get; init; }
}

public sealed record SkipDeliveryRequest
{
    public string Reason { get; init; } = "";
}

public sealed record RetryDeliveriesResponse(int Retried);

/// <summary>A kind of event a feed can send (#73), as the settings screen offers it.</summary>
public sealed record DeliveryKindDto(string Code, string Label, IReadOnlyList<string> Prefixes);

/// <summary>
/// A feed's settings (#73): everything but the secret, which stays in the API's configuration. <see cref="IsSaved"/> false: the feed runs on
/// its configuration, shown here as the starting point.
/// </summary>
public sealed record DeliveryFeedSettingsDto
{
    public required string Feed { get; init; }
    public required string Label { get; init; }
    public bool IsSaved { get; init; }
    public bool Enabled { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<string> EventKinds { get; init; } = [];

    /// <summary>The configuration's type prefixes no kind covers, kept only until the screen saves (it saves kinds).</summary>
    public IReadOnlyList<string> OtherEventTypes { get; init; } = [];
    public bool IncludeFiles { get; init; }
    public int MaxAttempts { get; init; }
    public int TimeoutSeconds { get; init; }
    public int PollSeconds { get; init; }
    public bool StartFromNow { get; init; }
    public bool SecretConfigured { get; init; }

    /// <summary>Where a receiver posts acknowledgements: <c>/api/integrations/{feed}/acknowledgements</c>.</summary>
    public required string AcknowledgementPath { get; init; }
    public IReadOnlyList<DeliveryKindDto> Kinds { get; init; } = [];
    public long Version { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record SaveDeliveryFeedSettingsRequest
{
    public long Version { get; init; }
    public bool Enabled { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<string> EventKinds { get; init; } = [];
    public bool IncludeFiles { get; init; }
    public int MaxAttempts { get; init; } = 12;
    public int TimeoutSeconds { get; init; } = 15;
    public int PollSeconds { get; init; } = 5;
    public bool StartFromNow { get; init; } = true;
}

/// <summary>
/// A receiver's acknowledgement of one event (#73), posted to <c>/api/integrations/{feed}/acknowledgements</c> and signed like Pulaski's
/// own messages: <c>X-Pulaski-Timestamp</c> and <c>X-Pulaski-Signature</c> (HMAC-SHA256 with the feed's secret of "{timestamp}.{body}").
/// </summary>
public sealed record AcknowledgementRequest
{
    public Guid EventId { get; init; }

    /// <summary><c>accepted</c> or <c>rejected</c>.</summary>
    public string Status { get; init; } = "";
    public string? Reference { get; init; }
    public string? Message { get; init; }
}

public sealed record AcknowledgementResponse(Guid DeliveryId, string DeliveryStatus);
