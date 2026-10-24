namespace Pulaski.Domain.Integrations;

/// <summary>Where an outbound delivery stands (D-061). Values match <c>evt.delivery.status</c>.</summary>
public static class DeliveryStatuses
{
    /// <summary>Waiting for its turn or its next attempt.</summary>
    public const string Pending = "pending";

    /// <summary>The receiver answered 2xx.</summary>
    public const string Delivered = "delivered";

    /// <summary>Given up on until someone retries or skips it; later events of the same subject wait behind it.</summary>
    public const string Failed = "failed";

    /// <summary>Someone decided it won't be delivered, with a reason; it no longer holds anything back.</summary>
    public const string Skipped = "skipped";
}

/// <summary>What an attempt leaves a delivery as: its status, when to try again, and why it failed.</summary>
public sealed record DeliveryOutcome(string Status, DateTimeOffset NextAttemptAt, string? Error);

/// <summary>
/// How deliveries are retried (D-061): a 2xx answer delivers; a 4xx other than a timeout or a rate limit can't succeed by trying
/// again and fails at once; anything else (5xx, timeouts, the receiver unreachable) is retried with doubling waits, from 10 s up to
/// an hour, until the attempts run out. Pure.
/// </summary>
public static class DeliveryPolicy
{
    public const int DefaultMaxAttempts = 12;
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    /// <param name="attempts">How many attempts have been made, this one included.</param>
    /// <param name="statusCode">The receiver's answer; null when there was none (unreachable, timed out).</param>
    /// <param name="error">What went wrong, when something did.</param>
    /// <param name="now">When the attempt was made.</param>
    /// <param name="maxAttempts">How many attempts before giving up.</param>
    public static DeliveryOutcome AfterAttempt(int attempts, int? statusCode, string? error, DateTimeOffset now, int maxAttempts = DefaultMaxAttempts)
    {
        if (statusCode is >= 200 and < 300)
        {
            return new DeliveryOutcome(DeliveryStatuses.Delivered, now, null);
        }

        var reason = statusCode is { } code ? $"The receiver answered {code}{(error is null ? "" : $": {error}")}" : error ?? "No answer from the receiver.";
        if (statusCode is >= 400 and < 500 and not (408 or 425 or 429))
        {
            return new DeliveryOutcome(DeliveryStatuses.Failed, now, $"{reason}. Trying again won't help until it is fixed at either end.");
        }

        if (attempts >= maxAttempts)
        {
            return new DeliveryOutcome(DeliveryStatuses.Failed, now, $"{reason}. Gave up after {attempts} attempts.");
        }

        var wait = TimeSpan.FromTicks(Math.Min(LongestWait.Ticks, FirstWait.Ticks * (1L << Math.Min(attempts - 1, 20))));
        return new DeliveryOutcome(DeliveryStatuses.Pending, now + wait, reason);
    }
}
