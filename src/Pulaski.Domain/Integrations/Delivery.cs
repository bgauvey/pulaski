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

/// <summary>A named set of event types a feed can send (#73): what screens offer instead of raw type prefixes.</summary>
public sealed record DeliveryKind(string Code, string Label, IReadOnlyList<string> Prefixes);

/// <summary>The kinds of events a feed can send (#73). Prefixes may overlap; an event goes once whichever kinds match it.</summary>
public static class DeliveryKinds
{
    public const string Inventory = "inventory";
    public const string Certificates = "certificates";
    public const string Orders = "orders";
    public const string Production = "production";
    public const string Labor = "labor";
    public const string Stops = "stops";
    public const string Quality = "quality";

    public static IReadOnlyList<DeliveryKind> All { get; } =
    [
        new(Inventory, "Inventory movements", ["pulaski.lot."]),
        new(Certificates, "Certificates and lot documents", ["pulaski.lot_document."]),
        new(Orders, "Work orders and jobs", ["pulaski.work_order.", "pulaski.job."]),
        new(Production, "Production reports and partial batches", ["pulaski.job.production_", "pulaski.partial_batch."]),
        new(Labor, "Labor sessions, breaks and approvals", ["pulaski.labor."]),
        new(Stops, "Machine stops coded and adjusted", ["pulaski.downtime."]),
        new(Quality, "Quality results", ["pulaski.quality_result."]),
    ];

    /// <summary>The type prefixes of the kinds, each once, in catalog order.</summary>
    public static IReadOnlyList<string> PrefixesOf(IEnumerable<string> kinds)
    {
        var wanted = kinds.ToHashSet(StringComparer.Ordinal);
        return [.. All.Where(k => wanted.Contains(k.Code)).SelectMany(k => k.Prefixes).Distinct()];
    }

    /// <summary>The kinds whose prefixes are all among <paramref name="prefixes"/>: a configuration's prefixes as the screen shows them.</summary>
    public static IReadOnlyList<string> KindsOf(IReadOnlyCollection<string> prefixes) =>
        [.. All.Where(k => k.Prefixes.All(prefixes.Contains)).Select(k => k.Code)];
}

/// <summary>What a receiver said it did with an event (#73). Values match <c>evt.delivery.ack_status</c>.</summary>
public static class AcknowledgementStatuses
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public static IReadOnlyList<string> All { get; } = [Accepted, Rejected];
}

/// <summary>Where an acknowledgement came from. Values match <c>evt.delivery.ack_source</c>.</summary>
public static class AcknowledgementSources
{
    /// <summary>In the body of the webhook's own 2xx answer.</summary>
    public const string Response = "response";

    /// <summary>Posted back later, signed with the feed's secret.</summary>
    public const string Callback = "callback";
}

/// <summary>
/// A receiver's answer about one event (#73): accepted or rejected, its own reference for what it booked, and a message. Rejected after
/// the HTTP delivery succeeded, the delivery fails and holds the subject's later events, as a refused delivery does.
/// </summary>
public sealed record Acknowledgement(string Status, string? Reference, string? Message)
{
    public const int MaxText = 500;

    /// <summary>
    /// An acknowledgement in a webhook's answer body: a JSON object with <c>status</c> <c>accepted</c> or <c>rejected</c>, and optional
    /// <c>reference</c> and <c>message</c>. Anything else (no body, not JSON, no or another status) is no acknowledgement.
    /// </summary>
    public static Acknowledgement? FromResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{')
        {
            return null;
        }

        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            string? Text(string name) =>
                json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? Trim(value.GetString()) : null;
            var status = Text("status")?.ToLowerInvariant();
            return status is not null && AcknowledgementStatuses.All.Contains(status) ? new Acknowledgement(status, Text("reference"), Text("message")) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public bool IsRejected => Status == AcknowledgementStatuses.Rejected;

    /// <summary>Why a rejected delivery failed, for its last error.</summary>
    public string RejectionText => $"Rejected by the receiver{(Message is { } m ? $": {m}" : ".")}";

    public static string? Trim(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var t && t.Length > MaxText ? t[..MaxText] : text.Trim();
}

/// <summary>
/// A feed's settings as kept from the screen (#73): everything but the secret. Checked here: an absolute http(s) address (plain http only for
/// this machine), known event kinds, and attempts, timeout and poll within range.
/// </summary>
public sealed record DeliveryFeedSettings(
    string Feed, bool Enabled, string? Url, IReadOnlyList<string> EventKinds, bool IncludeFiles, int MaxAttempts, int TimeoutSeconds, int PollSeconds,
    bool StartFromNow)
{
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Url is { } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            {
                problems.Add("The address must be a full https:// URL.");
            }
            else if (uri.Scheme == "http" && !uri.IsLoopback)
            {
                problems.Add("Use https:// for a receiver on another machine; plain http only for this one.");
            }
        }
        else if (Enabled)
        {
            problems.Add("Give the receiver's address before switching the feed on.");
        }

        if (EventKinds.Except(DeliveryKinds.All.Select(k => k.Code)).FirstOrDefault() is { } unknown)
        {
            problems.Add($"'{unknown}' isn't a kind of event.");
        }

        if (Enabled && EventKinds.Count == 0)
        {
            problems.Add("Choose at least one kind of event to send.");
        }

        if (MaxAttempts is < 1 or > 50)
        {
            problems.Add("Attempts must be 1 to 50.");
        }

        if (TimeoutSeconds is < 1 or > 120)
        {
            problems.Add("The timeout must be 1 to 120 seconds.");
        }

        if (PollSeconds is < 1 or > 300)
        {
            problems.Add("Checking for new events must be every 1 to 300 seconds.");
        }

        return problems;
    }
}
