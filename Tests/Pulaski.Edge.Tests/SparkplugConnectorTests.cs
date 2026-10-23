using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using Pulaski.Connectors;
using Pulaski.Connectors.Sparkplug;

namespace Pulaski.Edge.Tests;

/// <summary>The Sparkplug B connector against a real (in-process) MQTT broker (D-050).</summary>
public sealed class SparkplugConnectorTests : IAsyncLifetime
{
    private readonly int _port = 18830 + Random.Shared.Next(1000);
    private MqttServer _broker = null!;

    public async Task InitializeAsync()
    {
        _broker = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(_port).Build());
        await _broker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _broker.StopAsync();
        _broker.Dispose();
    }

    private sealed class Sink : ISampleSink
    {
        public ConcurrentQueue<ConnectorSample> Samples { get; } = new();

        public ConcurrentQueue<DiscoveredAddress> Discovered { get; } = new();

        public ValueTask PublishAsync(ConnectorSample sample, CancellationToken cancellationToken)
        {
            Samples.Enqueue(sample);
            return ValueTask.CompletedTask;
        }

        public ValueTask DiscoveredAsync(DiscoveredAddress address, CancellationToken cancellationToken)
        {
            Discovered.Enqueue(address);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task It_hears_a_node_asks_for_its_birth_and_reports_its_metrics_and_its_death()
    {
        using var node = new MqttFactory().CreateMqttClient();
        await node.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).WithClientId("simulator").Build());
        var commands = new ConcurrentQueue<string>();
        node.ApplicationMessageReceivedAsync += e =>
        {
            commands.Enqueue(e.ApplicationMessage.Topic);
            return Task.CompletedTask;
        };
        await node.SubscribeAsync("spBv1.0/Plant1/NCMD/Packing");

        var settings = new ConnectorSettings("BROKER", new Dictionary<string, string> { ["host"] = "127.0.0.1", ["port"] = _port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["groupId"] = "Plant1", ["hostId"] = "pulaski" }, new Dictionary<string, string>());
        await using var connector = new SparkplugConnectorFactory(TimeProvider.System).Create(settings, NullLoggerFactory.Instance);
        connector.Watch(new HashSet<string> { "Plant1/Packing/PACK-01:Status/StateCurrent" });
        var sink = new Sink();
        using var stop = new CancellationTokenSource();
        var running = connector.RunAsync(sink, stop.Token);
        await Until(() => _broker.GetClientsAsync().Result.Count == 2);

        // A bound node is asked for its birth on connecting, and again when its data comes before any birth.
        await Until(() => commands.Count == 1);
        await Publish(node, "spBv1.0/Plant1/DDATA/Packing/PACK-01", new SparkplugPayload(Now(), 3, [SparkplugMetric.Of("Status/StateCurrent", 6)]));
        await Until(() => commands.Count == 2);
        Assert.All(commands, c => Assert.Equal("spBv1.0/Plant1/NCMD/Packing", c));

        await Publish(node, "spBv1.0/Plant1/NBIRTH/Packing", new SparkplugPayload(Now(), 0, [SparkplugMetric.Of("bdSeq", 0L)]));
        await Publish(node, "spBv1.0/Plant1/DBIRTH/Packing/PACK-01", new SparkplugPayload(Now(), 1, [SparkplugMetric.Of("Status/StateCurrent", 4, alias: 1)]));
        await Publish(node, "spBv1.0/Plant1/DDATA/Packing/PACK-01", new SparkplugPayload(Now(), 2, [new SparkplugMetric(null, 1, Now(), SparkplugDataType.Int32, false, 6)]));
        await Until(() => sink.Samples.Any(s => s.Value is 6));
        Assert.Contains(sink.Discovered, d => d.Address == "Plant1/Packing/PACK-01:Status/StateCurrent");

        await Publish(node, "spBv1.0/Plant1/NDEATH/Packing", new SparkplugPayload(Now(), null, [SparkplugMetric.Of("bdSeq", 0L)]));
        await Until(() => sink.Samples.Any(s => s.Quality == SampleQuality.Bad));

        await stop.CancelAsync();
        await running;
    }

    private static ulong Now() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Task<MqttClientPublishResult> Publish(IMqttClient client, string topic, SparkplugPayload payload) =>
        client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload.Encode()).Build());
}
