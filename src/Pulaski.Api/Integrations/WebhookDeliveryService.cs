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

    /// <summary>The receiver's status code, or null with why when there was no answer.</summary>
    public async Task<(int? StatusCode, string? Error)> SendAsync(Uri url, string secret, OutboundEvent e, DateTimeOffset now, CancellationToken cancellationToken)
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
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var detail = response.IsSuccessStatusCode ? null : Trim(await response.Content.ReadAsStringAsync(cancellationToken));
            return ((int)response.StatusCode, detail);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Couldn't reach the receiver: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The receiver didn't answer in time.");
        }
    }

    private static string? Trim(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Length <= 300 ? text : text[..300] + "…";
}

/// <summary>
/// Relays events to one feed (D-061, #78), the ERP or a document system: every few seconds, while holding the sink's lock, it copies new matching events into the
/// delivery queue and sends what is due, in commit order, holding a subject's later events behind an undelivered one. An outage only
/// delays: everything stays queued and is retried with growing waits, then marked failed for someone to retry or skip.
/// </summary>
public sealed class WebhookDeliveryService(
    DeliveryRelay relay,
    IHttpClientFactory clients,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<WebhookDeliveryService> logger,
    string feed = WebhookFeedOptions.Erp) : BackgroundService
{
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = WebhookFeedOptions.From(configuration, feed);
        if (!options.Enabled || !Uri.TryCreate(options.Url, UriKind.Absolute, out var url) || string.IsNullOrEmpty(options.Secret))
        {
            if (options.Enabled)
            {
                logger.LogWarning("The {Feed} feed is enabled but its URL or secret is missing; nothing is delivered.", options.Label);
            }

            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)));
        do
        {
            try
            {
                await relay.WithLockAsync(options.Sink, () => RoundAsync(options, url, stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "The {Feed} feed couldn't read or update its queue; trying again shortly.", options.Label);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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

        var sender = new WebhookSender(clients.CreateClient(WebhookSender.ClientName));
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
            var (code, error) = await sender.SendAsync(url, options.Secret!, e, now, cancellationToken);
            var outcome = DeliveryPolicy.AfterAttempt(e.Attempts + 1, code, error, now, options.MaxAttempts);
            await relay.RecordAsync(e.DeliveryId, outcome, code, now, cancellationToken);
            if (outcome.Status != DeliveryStatuses.Delivered)
            {
                blocked.Add(e.Subject);
                logger.LogWarning("{Feed} delivery of {Type} {EventId} {Status}: {Error}", options.Label, e.Type, e.EventId, outcome.Status, outcome.Error);
            }
        }
    }
}
