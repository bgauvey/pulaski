using System.Collections.Concurrent;
using MQTTnet;
using MQTTnet.Client;
using Pulaski.Connectors.Sparkplug;

namespace Pulaski.DemoSetup;

/// <summary>
/// Starts the simulated PackML machines, as an operator would: Reset once a machine is Stopped, Start once it is Idle. Their states are
/// followed from their Sparkplug B births and data; a node is asked for a rebirth first, since it was born before this was listening.
/// </summary>
public sealed class MachineStarter(string host, int port)
{
    private const string StateMetric = "Status/StateCurrent";

    // The simulators' own numbers for these states, from their machine profile (not PackML's: their Idle is 1, PackML's 4).
    private static readonly int Stopped = Raw("Stopped"), Idle = Raw("Idle"), Execute = Raw("Execute");

    private readonly ConcurrentDictionary<string, int> _states = new(StringComparer.Ordinal);

    public async Task StartAllAsync(IReadOnlyList<DemoPlan.Machine> machines, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new MqttFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            // spBv1.0/<group>/<DBIRTH|DDATA>/<node>/<device>
            var parts = e.ApplicationMessage.Topic.Split('/');
            if (parts.Length == 5)
            {
                var payload = SparkplugPayload.Decode(e.ApplicationMessage.PayloadSegment);
                if (payload.Metrics.FirstOrDefault(m => m.Name == StateMetric) is { Value: { } value })
                {
                    _states[parts[4]] = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return Task.CompletedTask;
        };

        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId($"pulaski-demo-setup-{Guid.NewGuid():N}"[..30]).Build(), ct);
        await client.SubscribeAsync($"spBv1.0/{DemoPlan.GroupId}/DBIRTH/#", cancellationToken: ct);
        await client.SubscribeAsync($"spBv1.0/{DemoPlan.GroupId}/DDATA/#", cancellationToken: ct);
        foreach (var node in machines.Select(m => m.Node).Distinct())
        {
            await PublishAsync(client, $"spBv1.0/{DemoPlan.GroupId}/NCMD/{node}", "Node Control/Rebirth", ct);
        }

        await Task.WhenAll(machines.Select(m => StartAsync(client, m, timeout, ct)));
        await client.DisconnectAsync(cancellationToken: ct);
    }

    private async Task StartAsync(IMqttClient client, DemoPlan.Machine machine, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        var commands = $"spBv1.0/{DemoPlan.GroupId}/DCMD/{machine.Node}/{machine.Device}";
        var lastCommand = DateTimeOffset.MinValue;
        while (DateTimeOffset.UtcNow < until)
        {
            var state = _states.TryGetValue(machine.Device, out var s) ? s : (int?)null;
            if (state == Execute)
            {
                Console.WriteLine($"{machine.Device} is running (Execute).");
                return;
            }

            // A command takes the simulator 1-10 s; don't repeat one before it has had the chance.
            if (DateTimeOffset.UtcNow - lastCommand > TimeSpan.FromSeconds(15) && (state == Stopped || state == Idle))
            {
                var command = state == Stopped ? "Command/Reset" : "Command/Start";
                await PublishAsync(client, commands, command, ct);
                Console.WriteLine($"{machine.Device}: {(state == Stopped ? "Stopped" : "Idle")}, sent {command}.");
                lastCommand = DateTimeOffset.UtcNow;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        Console.WriteLine($"{machine.Device} didn't reach Execute within {timeout.TotalSeconds:0} s (last state {(_states.TryGetValue(machine.Device, out var last) ? last : "unknown")}); start it as the user guide describes.");
    }

    private static int Raw(string packMlState) =>
        DemoPlan.RawState(DemoPlan.ProfileCsv, packMlState) ?? throw new InvalidOperationException($"The simulators' profile has no {packMlState} state.");

    private static Task<MqttClientPublishResult> PublishAsync(IMqttClient client, string topic, string metric, CancellationToken ct) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(new SparkplugPayload((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, [SparkplugMetric.Of(metric, true)]).Encode())
            .Build(), ct);
}
