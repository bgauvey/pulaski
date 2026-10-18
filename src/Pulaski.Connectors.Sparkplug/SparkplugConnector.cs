using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Pulaski.Connectors.Sparkplug;

/// <summary>Makes Sparkplug B connectors (D-041).</summary>
public sealed class SparkplugConnectorFactory(TimeProvider time) : IConnectorFactory
{
    public string Protocol => "sparkplug_b";

    public IConnector Create(ConnectorSettings settings, ILoggerFactory loggers) =>
        new SparkplugConnector(SparkplugOptions.From(settings), time, loggers.CreateLogger<SparkplugConnector>());
}

/// <summary>A Sparkplug B connection's settings. The password comes from the edge's configuration, never from Pulaski.</summary>
public sealed record SparkplugOptions(string Host, int Port, bool Tls, string ClientId, string? GroupId, string? HostId, string? Username, string? Password)
{
    public static SparkplugOptions From(ConnectorSettings settings)
    {
        var tls = settings.Get("tls") == "true";
        return new SparkplugOptions(
            settings.Get("host") ?? throw new InvalidOperationException($"Connection {settings.ConnectionCode} has no host."),
            settings.Get("port") is { } port ? int.Parse(port, CultureInfo.InvariantCulture) : tls ? 8883 : 1883,
            tls,
            settings.Get("clientId") ?? $"pulaski-{settings.ConnectionCode.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..8]}",
            settings.Get("groupId"),
            settings.Get("hostId"),
            settings.Get("username"),
            settings.Secret("password"));
    }
}

/// <summary>
/// A Sparkplug B host application (D-041): subscribes to the namespace (or one group), turns births, data and deaths into
/// samples, asks nodes for a rebirth when needed, and with a host id publishes its STATE so edge nodes know it is listening.
/// It reconnects by itself; while the broker is away every address it heard is reported lost.
/// </summary>
public sealed class SparkplugConnector(SparkplugOptions options, TimeProvider time, ILogger<SparkplugConnector> log) : IConnector
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly SparkplugHost _host = new(time);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private HashSet<(string Group, string Node)> _watched = [];
    private IMqttClient? _client;

    /// <summary>
    /// Everything is read and the edge drops what isn't bound, since Sparkplug sends a node's metrics together. But the nodes the
    /// bound addresses name are asked for a rebirth on connecting, and when first bound, so their current values arrive even
    /// while a machine sits idle and sends nothing.
    /// </summary>
    public void Watch(IReadOnlySet<string> addresses)
    {
        var nodes = addresses.Select(NodeOf).OfType<(string, string)>().ToHashSet();
        HashSet<(string, string)> added;
        lock (_lock)
        {
            added = [.. nodes.Except(_watched)];
            _watched = nodes;
        }

        if (added.Count > 0 && _client is { IsConnected: true } client)
        {
            _ = RebirthAsync(client, added, CancellationToken.None);
        }
    }

    /// <summary>The group and node of a <c>group/node[/device]:metric</c> address.</summary>
    internal static (string Group, string Node)? NodeOf(string address)
    {
        var path = address.Split(':', 2)[0].Split('/');
        return path.Length >= 2 ? (path[0], path[1]) : null;
    }

    public async Task RunAsync(ISampleSink sink, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = new MqttFactory().CreateMqttClient();
            _client = client;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.DisconnectedAsync += _ =>
            {
                disconnected.TrySetResult();
                return Task.CompletedTask;
            };
            client.ApplicationMessageReceivedAsync += e => ReceiveAsync(client, sink, e.ApplicationMessage, cancellationToken);

            try
            {
                await client.ConnectAsync(ClientOptions(), cancellationToken);
                if (options.HostId is { } hostId)
                {
                    await client.PublishAsync(State(hostId, online: true), cancellationToken);
                }

                var filter = $"{SparkplugTopic.Namespace}/{options.GroupId ?? "+"}/#";
                await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter, MqttQualityOfServiceLevel.AtLeastOnce).Build(), cancellationToken);
                log.LogInformation("Sparkplug B: connected to {Host}:{Port}, listening on {Filter}", options.Host, options.Port, filter);
                HashSet<(string, string)> watched;
                lock (_lock)
                {
                    watched = [.. _watched];
                }

                await RebirthAsync(client, watched, cancellationToken);
                backoff = TimeSpan.FromSeconds(1);
                await disconnected.Task.WaitAsync(cancellationToken);
                log.LogWarning("Sparkplug B: lost the broker at {Host}:{Port}", options.Host, options.Port);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is MQTTnet.Exceptions.MqttCommunicationException or MQTTnet.Adapter.MqttConnectingFailedException or IOException or TimeoutException)
            {
                log.LogWarning("Sparkplug B: can't reach {Host}:{Port} ({Message}); trying again in {Seconds} s", options.Host, options.Port, ex.Message, backoff.TotalSeconds);
            }
            finally
            {
                _client = null;
            }

            await PublishAllAsync(sink, _host.LoseAll(), CancellationToken.None);
            try
            {
                await Task.Delay(backoff, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private MqttClientOptions ClientOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port)
            .WithClientId(options.ClientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
        if (options.Tls)
        {
            builder = builder.WithTlsOptions(o => o.UseTls());
        }

        if (options.Username is { } user)
        {
            builder = builder.WithCredentials(user, options.Password);
        }

        if (options.HostId is { } hostId)
        {
            var offline = State(hostId, online: false);
            builder = builder.WithWillTopic(offline.Topic).WithWillPayload(offline.PayloadSegment).WithWillRetain().WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        }

        return builder.Build();
    }

    // Sparkplug 3.0: the primary host announces itself on spBv1.0/STATE/{host id}, retained, in JSON.
    private MqttApplicationMessage State(string hostId, bool online) => new MqttApplicationMessageBuilder()
        .WithTopic($"{SparkplugTopic.Namespace}/STATE/{hostId}")
        .WithPayload(Encoding.UTF8.GetBytes($"{{\"online\":{(online ? "true" : "false")},\"timestamp\":{time.GetUtcNow().ToUnixTimeMilliseconds()}}}"))
        .WithRetainFlag()
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .Build();

    private async Task ReceiveAsync(IMqttClient client, ISampleSink sink, MqttApplicationMessage message, CancellationToken cancellationToken)
    {
        SparkplugOutcome outcome;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            outcome = _host.Handle(message.Topic, message.PayloadSegment.AsSpan());
        }
        finally
        {
            _gate.Release();
        }

        foreach (var discovered in outcome.Discovered)
        {
            await sink.DiscoveredAsync(discovered, cancellationToken);
        }

        await PublishAllAsync(sink, outcome.Samples, cancellationToken);
        if (outcome.Rebirth is { } node && client.IsConnected)
        {
            await RebirthAsync(client, [node], cancellationToken);
        }
    }

    private async Task RebirthAsync(IMqttClient client, IEnumerable<(string Group, string Node)> nodes, CancellationToken cancellationToken)
    {
        foreach (var (group, node) in nodes)
        {
            try
            {
                log.LogInformation("Sparkplug B: asking {Group}/{Node} for its birth", group, node);
                await client.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic(SparkplugTopic.NodeCommand(group, node))
                    .WithPayload(_host.RebirthCommand())
                    .Build(), cancellationToken);
            }
            catch (Exception ex) when (ex is MQTTnet.Exceptions.MqttCommunicationException or InvalidOperationException)
            {
                // Not connected after all: asked again on the next connection.
                log.LogDebug("Sparkplug B: couldn't ask {Group}/{Node} for its birth: {Message}", group, node, ex.Message);
            }
        }
    }

    private static async Task PublishAllAsync(ISampleSink sink, IReadOnlyList<ConnectorSample> samples, CancellationToken cancellationToken)
    {
        foreach (var sample in samples)
        {
            await sink.PublishAsync(sample, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { IsConnected: true } client)
        {
            if (options.HostId is { } hostId)
            {
                await client.PublishAsync(State(hostId, online: false));
            }

            await client.DisconnectAsync();
        }

        _gate.Dispose();
    }
}
