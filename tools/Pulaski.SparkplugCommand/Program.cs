using System.Globalization;
using MQTTnet;
using MQTTnet.Client;
using Pulaski.Connectors.Sparkplug;

// Sends one Sparkplug B command (NCMD or DCMD) with one or more metrics, then exits.
//
//   dotnet run --project tools/Pulaski.SparkplugCommand -- [--host localhost] [--port 1883] [--delay-seconds 1]
//       [--user u --password p] spBv1.0/Plant1/DCMD/Extruder/EXT-01-EXT Command/Reset=true Command/Start=true
//
// Values: true/false, whole numbers, decimals, or text. Each metric is sent in its own message, with a delay between
// them so a PackML machine has time to change state between commands.
var host = "localhost";
var port = 1883;
var delaySeconds = 1;
string? user = null, password = null, topic = null;
var metrics = new List<(string Name, object Value)>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host":
            host = args[++i];
            break;
        case "--port":
            port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--delay-seconds":
            if (i + 1 >= args.Length
                || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out delaySeconds)
                || delaySeconds is < 1 or > 300)
            {
                await Console.Error.WriteLineAsync("The delay is a whole number of seconds from 1 to 300.");
                return 2;
            }

            break;
        case "--user":
            user = args[++i];
            break;
        case "--password":
            password = args[++i];
            break;
        default:
            if (topic is null)
            {
                topic = args[i];
            }
            else if (args[i].Split('=', 2) is [var name, var text])
            {
                metrics.Add((name, Parse(text)));
            }

            break;
    }
}

if (topic is null || metrics.Count == 0 || !topic.Contains("CMD/", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync("Usage: [--host h] [--port p] [--delay-seconds 1] [--user u --password p] spBv1.0/<group>/DCMD/<node>/<device> Metric=value ...");
    return 2;
}

using var client = new MqttFactory().CreateMqttClient();
var options = new MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId($"pulaski-command-{Guid.NewGuid():N}"[..30]);
if (user is not null)
{
    options = options.WithCredentials(user, password);
}

await client.ConnectAsync(options.Build());
for (var i = 0; i < metrics.Count; i++)
{
    var (name, value) = metrics[i];
    var payload = new SparkplugPayload((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, [SparkplugMetric.Of(name, value)]).Encode();
    await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build());
    await Console.Out.WriteLineAsync($"{topic} {name}={value}");
    if (i < metrics.Count - 1)
    {
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
    }
}

await client.DisconnectAsync();
return 0;

static object Parse(string text) =>
    bool.TryParse(text, out var b) ? b
    : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
    : text;
