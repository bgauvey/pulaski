using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Pulaski.Domain.Integrations;
using Pulaski.Infrastructure.Integrations;

namespace Pulaski.Api.Integrations;

/// <summary>
/// Sends one event to a feed's webhook (D-061) as a CloudEvents 1.0 message in structured JSON. The event id is also the
/// <c>Idempotency-Key</c>, so a receiver can drop a repeat; the body is signed with the shared secret as
/// <c>X-Pulaski-Signature: sha256=hex(HMAC(secret, "{timestamp}.{body}"))</c> with the timestamp in <c>X-Pulaski-Timestamp</c>.
/// </summary>
public sealed class WebhookSender(HttpClient http)
{
    public const string ClientName = "erp-webhook";

    /// <summary>The largest file sent with a certificate's event (#78), as stored documents are (D-056).</summary>
    public const int MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>
    /// The event with a certificate's file in <c>data.file</c> (#78): name, type, size and the bytes as base64. A file over
    /// <see cref="MaxFileBytes"/> goes as a note instead, so the receiver knows to fetch it another way.
    /// </summary>
    public static OutboundEvent WithFile(OutboundEvent e, string fileName, string contentType, byte[] content)
    {
        var data = JsonNode.Parse(e.Data) as JsonObject ?? [];
        data["file"] = content.Length > MaxFileBytes
            ? new JsonObject { ["fileName"] = fileName, ["contentType"] = contentType, ["sizeBytes"] = content.Length, ["omitted"] = "larger than 10 MB" }
            : new JsonObject
            {
                ["fileName"] = fileName,
                ["contentType"] = contentType,
                ["sizeBytes"] = content.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(content)),
                ["contentBase64"] = Convert.ToBase64String(content),
            };
        return e with { Data = data.ToJsonString() };
    }

    public static string Body(OutboundEvent e) => new JsonObject
    {
        ["specversion"] = "1.0",
        ["id"] = e.EventId.ToString(),
        ["source"] = e.Source,
        ["type"] = e.Type,
        ["subject"] = e.Subject.ToString(),
        ["time"] = e.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        ["datacontenttype"] = "application/json",
        ["pulaskisubjecttype"] = e.SubjectType,
        ["pulaskisequence"] = e.Sequence,
        ["pulaskiactor"] = e.ActorId,
        ["pulaskicorrelationid"] = e.CorrelationId?.ToString(),
        ["pulaskireason"] = e.Reason,
        ["data"] = JsonNode.Parse(e.Data),
    }.ToJsonString();

    public static string Signature(string secret, string timestamp, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    /// <summary>
    /// The receiver's status code, or null with why when there was no answer; with a 2xx, any acknowledgement in its body (#73). The attempt
    /// gives up after <paramref name="timeout"/> (default: the client's own).
    /// </summary>
    public async Task<(int? StatusCode, string? Error, Acknowledgement? Acknowledgement)> SendAsync(
        Uri url, string secret, OutboundEvent e, DateTimeOffset now, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var body = Body(e);
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/cloudevents+json")),
        };
        request.Headers.Add("Idempotency-Key", e.EventId.ToString());
        request.Headers.Add("X-Pulaski-Timestamp", timestamp);
        request.Headers.Add("X-Pulaski-Signature", Signature(secret, timestamp, body));
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            attempt.CancelAfter(limit);
        }

        try
        {
            using var response = await http.SendAsync(request, attempt.Token);
            var text = await response.Content.ReadAsStringAsync(attempt.Token);
            return response.IsSuccessStatusCode
                ? ((int)response.StatusCode, null, Acknowledgement.FromResponse(text))
                : ((int)response.StatusCode, Trim(text), null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Couldn't reach the receiver: {ex.Message}", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The receiver didn't answer in time.", null);
        }
    }

    private static string? Trim(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Length <= 300 ? text : text[..300] + "…";
}

/// <summary>
/// Relays events to every feed (D-061, #73, #74), the built-in ERP and document feeds and those added on the screen, webhook or MQTT. Each
/// tick it lists the feeds, and runs a round for each that is ready and due (its own poll interval), side by side, each under its sink's lock
/// so several API servers never send one feed at once. A round copies new matching events into the queue and sends what is due, in commit
/// order, holding a subject's later events behind an undelivered one. An outage only delays.
/// </summary>
public sealed class WebhookDeliveryService(
    DeliveryRelay relay,
    IHttpClientFactory clients,
    MqttFeedSender mqtt,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<WebhookDeliveryService> logger) : BackgroundService
{
    private const int Batch = 200;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _next = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    // Feeds and their settings are read each tick (#73, #74): a change, or a new feed, takes effect without a restart.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var saved = (await relay.ListFeedSettingsAsync(stoppingToken)).ToDictionary(f => f.Feed, StringComparer.Ordinal);
                foreach (var feed in WebhookFeedOptions.Feeds.Union(saved.Keys))
                {
                    Launch(WebhookFeedOptions.From(configuration, feed).Apply(saved.GetValueOrDefault(feed)), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "The delivery relay couldn't read its feeds; trying again shortly.");
            }

            await Task.Delay(Tick, time, stoppingToken);
        }

        await Task.WhenAll(_running.Values);
    }

    private void Launch(WebhookFeedOptions options, CancellationToken stoppingToken)
    {
        if (!options.IsReady(out var url))
        {
            if (options.Enabled && _warned.Add(options.Feed))
            {
                logger.LogWarning("The {Feed} feed is on but its address or secret is missing; nothing is delivered.", options.Label);
            }

            return;
        }

        _warned.Remove(options.Feed);
        var now = time.GetUtcNow();
        if (_running.TryGetValue(options.Feed, out var running) && !running.IsCompleted || _next.GetValueOrDefault(options.Feed) > now)
        {
            return;
        }

        _next[options.Feed] = now.AddSeconds(Math.Max(1, options.PollSeconds));
        _running[options.Feed] = Task.Run(async () =>
        {
            try
            {
                await relay.WithLockAsync(options.Sink, () => RoundAsync(options, url!, stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "The {Feed} feed couldn't read or update its queue; trying again shortly.", options.Label);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);
    }

    /// <summary>One round: capture what is new, then send what is due. Public for tests.</summary>
    public async Task RoundAsync(WebhookFeedOptions options, Uri url, CancellationToken cancellationToken)
    {
        int scanned;
        do
        {
            (scanned, _) = await relay.CaptureAsync(options.Sink, options.EventTypes, options.StartFromNow, Batch, cancellationToken);
        }
        while (scanned == Batch);

        var webhook = new WebhookSender(clients.CreateClient(WebhookSender.ClientName));
        var blocked = new HashSet<Guid>();
        foreach (var due in await relay.DueAsync(options.Sink, time.GetUtcNow(), Batch, cancellationToken))
        {
            // A subject whose earlier event just failed waits; the query only knew about earlier rounds.
            if (blocked.Contains(due.Subject))
            {
                continue;
            }

            // A certificate's file goes with it when the feed sends files (#78), read now: stored files never change.
            var e = options.SendsFiles && due.Type == Application.Abstractions.EventTypes.LotDocumentAttached
                && await relay.GetDocumentFileAsync(due.Subject, cancellationToken) is { } file
                ? WebhookSender.WithFile(due, file.FileName, file.ContentType, file.Content)
                : due;

            var now = time.GetUtcNow();
            var (code, error, acknowledgement) = options.Kind == DeliveryFeedKinds.Mqtt
                ? await PublishAsync(options, url, e, now, cancellationToken)
                : await webhook.SendAsync(url, options.Secret!, e, now, cancellationToken, TimeSpan.FromSeconds(options.TimeoutSeconds));
            var outcome = DeliveryPolicy.AfterAttempt(e.Attempts + 1, code, error, now, options.MaxAttempts);

            // Delivered but rejected in the answer (#73): it fails as a refused delivery does, holding the subject's later events.
            if (outcome.Status == DeliveryStatuses.Delivered && acknowledgement is { IsRejected: true })
            {
                outcome = new DeliveryOutcome(DeliveryStatuses.Failed, now, acknowledgement.RejectionText);
            }

            await relay.RecordAsync(e.DeliveryId, outcome, code, now, cancellationToken, acknowledgement);
            if (outcome.Status != DeliveryStatuses.Delivered)
            {
                blocked.Add(e.Subject);
                logger.LogWarning("{Feed} delivery of {Type} {EventId} {Status}: {Error}", options.Label, e.Type, e.EventId, outcome.Status, outcome.Error);
            }
        }
    }

    // A broker gives no acknowledgement in its answer; a receiver behind it can still post one back.
    private async Task<(int? StatusCode, string? Error, Acknowledgement? Acknowledgement)> PublishAsync(
        WebhookFeedOptions options, Uri url, OutboundEvent e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (code, error) = await mqtt.SendAsync(options, url, e, now, cancellationToken);
        return (code, error, null);
    }
}

/// <summary>
/// Clears delivered rows older than <c>Retention:DeliveredDays</c> (90 by default, at least 7) once an hour (#74), through
/// <c>evt.purge_delivered</c>. Failed, pending and skipped rows stay, and the event log is never touched. Failures are retried next time.
/// </summary>
internal sealed class DeliveryRetentionService(DeliveryRelay relay, IConfiguration configuration, ILogger<DeliveryRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var keepDays = Math.Max(7, configuration.GetValue("Retention:DeliveredDays", 90));
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var deleted = await relay.PurgeDeliveredAsync(keepDays, stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Cleared {Count} deliveries delivered more than {Days} days ago.", deleted, keepDays);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't clear old deliveries; trying again in {Interval}.", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
