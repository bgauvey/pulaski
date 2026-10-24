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
