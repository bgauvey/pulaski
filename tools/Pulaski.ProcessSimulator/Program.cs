using System.Globalization;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Pulaski.ProcessSimulator;

// The demo's process-value simulator (D-101): a Sparkplug B edge node for the extruder EXT-01-EXT, publishing its melt temperature (°C)
// and the profile's width (mm) with common-cause noise and, now and then, a shift in the melt temperature that a control chart notices.
//
//   PULASKI_MQTT_HOST / PORT        the broker (default broker:1883)
//   PULASKI_SIM_GROUP / NODE / DEVICE   Sparkplug names (default Plant1 / ExtruderProcess / EXT-01-EXT)
//   PULASKI_SIM_INTERVAL_SECONDS    how often it publishes (default 2)
//   PULASKI_SIM_SHIFT_EVERY_MINUTES a melt-temperature shift at the end of every so many minutes (default 30; 0 = never)
//   PULASKI_SIM_SHIFT_MINUTES       how long it lasts (default 10)
//   PULASKI_SIM_SHIFT_SIGMA         how far it moves the mean, in sigma (default 1.5)
//   PULASKI_SIM_SEED                a seed for repeatable noise (default: random)
var host = Setting("PULASKI_MQTT_HOST", "broker");
var port = Number("PULASKI_MQTT_PORT", 1883);
var interval = TimeSpan.FromSeconds(Number("PULASKI_SIM_INTERVAL_SECONDS", 2));
var shift = new ProcessShift(
    "Process/MeltTemperature",
    TimeSpan.FromMinutes(Number("PULASKI_SIM_SHIFT_EVERY_MINUTES", 30)),
    TimeSpan.FromMinutes(Number("PULASKI_SIM_SHIFT_MINUTES", 10)),
    Real("PULASKI_SIM_SHIFT_SIGMA", 1.5));
var random = Environment.GetEnvironmentVariable("PULASKI_SIM_SEED") is { Length: > 0 } seed
    ? new Random(int.Parse(seed, CultureInfo.InvariantCulture))
    : new Random();
var model = new ProcessModel(ProcessModel.Extruder, shift, random);
var node = new SparkplugNode(Setting("PULASKI_SIM_GROUP", "Plant1"), Setting("PULASKI_SIM_NODE", "ExtruderProcess"), Setting("PULASKI_SIM_DEVICE", "EXT-01-EXT"), TimeProvider.System);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

var started = DateTimeOffset.UtcNow;
var rebirth = 0;
ulong session = 0;
Console.WriteLine($"Simulating {string.Join(" and ", model.Variables.Select(v => $"{v.Metric} ({v.Target} ± {v.Sigma})"))} every {interval.TotalSeconds:0.#} s; "
    + (shift.Every > TimeSpan.Zero ? $"melt temperature shifts {shift.SizeInSigma} sigma for {shift.Length.TotalMinutes:0} min of every {shift.Every.TotalMinutes:0}." : "no shifts."));

while (!stop.IsCancellationRequested)
{
    using var client = new MqttFactory().CreateMqttClient();
    try
    {
        var (deathTopic, death) = node.NewSession(session++);
        client.ApplicationMessageReceivedAsync += e =>
        {
            if (SparkplugNode.AsksForRebirth(e.ApplicationMessage.PayloadSegment))
            {
                Interlocked.Exchange(ref rebirth, 1);
            }

            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"pulaski-process-sim-{Guid.NewGuid():N}"[..30])
            .WithWillTopic(deathTopic)
            .WithWillPayload(death)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), stop.Token);
        await client.SubscribeAsync(node.CommandTopic, cancellationToken: stop.Token);
        Console.WriteLine($"Connected to {host}:{port}; publishing.");

        await PublishAsync(client, node.Births(model.Read(DateTimeOffset.UtcNow - started)), stop.Token);
        using var timer = new PeriodicTimer(interval);
        while (client.IsConnected && await timer.WaitForNextTickAsync(stop.Token))
        {
            var values = model.Read(DateTimeOffset.UtcNow - started);
            if (Interlocked.Exchange(ref rebirth, 0) == 1)
            {
                await PublishAsync(client, node.Births(values), stop.Token);
            }
            else
            {
                await PublishAsync(client, [node.Data(values)], stop.Token);
            }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex) when (ex is MQTTnet.Exceptions.MqttCommunicationException or System.Net.Sockets.SocketException)
    {
        Console.WriteLine($"The broker at {host}:{port} isn't answering ({ex.Message}); trying again in 5 s.");
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

return 0;

static async Task PublishAsync(IMqttClient client, IReadOnlyList<(string Topic, byte[] Payload)> messages, CancellationToken ct)
{
    foreach (var (topic, payload) in messages)
    {
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build(), ct);
    }
}

static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

static int Number(string name, int fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

static double Real(string name, double fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;
