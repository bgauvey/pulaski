using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Pulaski.Domain.Integrations;
using Pulaski.Infrastructure.Integrations;

namespace Pulaski.Api.Integrations;

/// <summary>
/// Publishes events to an MQTT feed's broker (#74): one QoS 1 message per event, the same CloudEvents JSON as a webhook, on a topic from the
/// feed's template, with the event id (and, with a secret, the signature) as MQTT 5 user properties. Delivered when the broker acknowledges.
/// One connection per feed, kept open and remade when its address or sign-in changes.
/// </summary>
public sealed class MqttFeedSender : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (string Key, IMqttClient Client)> _clients = new(StringComparer.Ordinal);
    private readonly MqttFactory _factory = new();

    /// <summary>The broker's answer as an HTTP-like code for the delivery policy (200 delivered, 4xx refused, 5xx retried), or null with why.</summary>
    public async Task<(int? StatusCode, string? Error)> SendAsync(WebhookFeedOptions options, Uri url, OutboundEvent e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        try
        {
            var client = await ConnectedAsync(options, url, attempt.Token);
            var body = WebhookSender.Body(e);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(DeliveryFeedKinds.Topic(options.Topic ?? "pulaski/{type}", e.Type, e.SubjectType, e.Subject))
                .WithPayload(Encoding.UTF8.GetBytes(body))
                .WithContentType("application/cloudevents+json")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithUserProperty("Idempotency-Key", e.EventId.ToString());
            if (!string.IsNullOrEmpty(options.Secret))
            {
                var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                message = message.WithUserProperty("X-Pulaski-Timestamp", timestamp).WithUserProperty("X-Pulaski-Signature", WebhookSender.Signature(options.Secret, timestamp, body));
            }

            var result = await client.PublishAsync(message.Build(), attempt.Token);
            return Code(result.ReasonCode, result.ReasonString);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DropAsync(options.Feed);
            return (null, "The broker didn't answer in time.");
        }
        catch (Exception ex) when (ex is MQTTnet.Exceptions.MqttCommunicationException or MQTTnet.Adapter.MqttConnectingFailedException or InvalidOperationException)
        {
            await DropAsync(options.Feed);
            return (null, $"Couldn't reach the broker: {ex.Message}");
        }
    }

    /// <summary>A refused publish can't succeed by retrying (bad topic, not allowed); a busy broker can.</summary>
    public static (int? StatusCode, string? Error) Code(MqttClientPublishReasonCode reason, string? text) => reason switch
    {
        MqttClientPublishReasonCode.Success or MqttClientPublishReasonCode.NoMatchingSubscribers => (200, null),
        MqttClientPublishReasonCode.NotAuthorized => (403, text ?? "The broker doesn't allow publishing there."),
        MqttClientPublishReasonCode.TopicNameInvalid or MqttClientPublishReasonCode.PayloadFormatInvalid => (400, text ?? $"The broker refused it: {reason}."),
        MqttClientPublishReasonCode.QuotaExceeded => (429, text ?? "The broker's quota is exceeded."),
        _ => (503, text ?? $"The broker answered {reason}."),
    };

    private async Task<IMqttClient> ConnectedAsync(WebhookFeedOptions options, Uri url, CancellationToken cancellationToken)
    {
        var key = $"{url}|{options.Username}|{options.Password?.GetHashCode(StringComparison.Ordinal)}";
        if (_clients.TryGetValue(options.Feed, out var existing) && existing.Key == key && existing.Client.IsConnected)
        {
            return existing.Client;
        }

        await DropAsync(options.Feed);
        var tls = url.Scheme == "mqtts";
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(url.Host, url.IsDefaultPort || url.Port < 0 ? (tls ? 8883 : 1883) : url.Port)
            .WithClientId($"pulaski-feed-{options.Feed}-{Environment.MachineName}".ToLowerInvariant())
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
        if (tls)
        {
            builder = builder.WithTlsOptions(o => o.UseTls());
        }

        if (options.Username is { } user)
        {
            builder = builder.WithCredentials(user, options.Password);
        }

        var client = _factory.CreateMqttClient();
        await client.ConnectAsync(builder.Build(), cancellationToken);
        _clients[options.Feed] = (key, client);
        return client;
    }

    private async Task DropAsync(string feed)
    {
        if (_clients.TryRemove(feed, out var old))
        {
            if (old.Client.IsConnected)
            {
                await old.Client.DisconnectAsync();
            }

            old.Client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var feed in _clients.Keys)
        {
            await DropAsync(feed);
        }
    }
}
