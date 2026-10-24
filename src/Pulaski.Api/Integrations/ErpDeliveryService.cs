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
/// Sends one event to the ERP's webhook (D-061) as a CloudEvents 1.0 message in structured JSON. The event id is also the
/// <c>Idempotency-Key</c>, so a receiver can drop a repeat; the body is signed with the shared secret as
/// <c>X-Pulaski-Signature: sha256=hex(HMAC(secret, "{timestamp}.{body}"))</c> with the timestamp in <c>X-Pulaski-Timestamp</c>.
/// </summary>
public sealed class ErpWebhookSender(HttpClient http)
{
    public const string ClientName = "erp-webhook";

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
/// Relays events to the ERP (D-061): every few seconds, while holding the sink's lock, it copies new matching events into the
/// delivery queue and sends what is due, in commit order, holding a subject's later events behind an undelivered one. An outage only
/// delays: everything stays queued and is retried with growing waits, then marked failed for someone to retry or skip.
/// </summary>
public sealed class ErpDeliveryService(
    DeliveryRelay relay,
    IHttpClientFactory clients,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<ErpDeliveryService> logger) : BackgroundService
{
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = ErpDeliveryOptions.From(configuration);
        if (!options.Enabled || !Uri.TryCreate(options.Url, UriKind.Absolute, out var url) || string.IsNullOrEmpty(options.Secret))
        {
            if (options.Enabled)
            {
                logger.LogWarning("The ERP feed is enabled but its URL or secret is missing; nothing is delivered.");
            }

            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)));
        do
        {
            try
            {
                await relay.WithLockAsync(ErpDeliveryOptions.SinkName, () => RoundAsync(options, url, stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "The ERP feed couldn't read or update its queue; trying again shortly.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One round: capture what is new, then send what is due. Public for tests.</summary>
    public async Task RoundAsync(ErpDeliveryOptions options, Uri url, CancellationToken cancellationToken)
    {
        int scanned;
        do
        {
            (scanned, _) = await relay.CaptureAsync(ErpDeliveryOptions.SinkName, options.EventTypes, options.StartFromNow, Batch, cancellationToken);
        }
        while (scanned == Batch);

        var sender = new ErpWebhookSender(clients.CreateClient(ErpWebhookSender.ClientName));
        var blocked = new HashSet<Guid>();
        foreach (var e in await relay.DueAsync(ErpDeliveryOptions.SinkName, time.GetUtcNow(), Batch, cancellationToken))
        {
            // A subject whose earlier event just failed waits; the query only knew about earlier rounds.
            if (blocked.Contains(e.Subject))
            {
                continue;
            }

            var now = time.GetUtcNow();
            var (code, error) = await sender.SendAsync(url, options.Secret!, e, now, cancellationToken);
            var outcome = DeliveryPolicy.AfterAttempt(e.Attempts + 1, code, error, now, options.MaxAttempts);
            await relay.RecordAsync(e.DeliveryId, outcome, code, now, cancellationToken);
            if (outcome.Status != DeliveryStatuses.Delivered)
            {
                blocked.Add(e.Subject);
                logger.LogWarning("ERP delivery of {Type} {EventId} {Status}: {Error}", e.Type, e.EventId, outcome.Status, outcome.Error);
            }
        }
    }
}
