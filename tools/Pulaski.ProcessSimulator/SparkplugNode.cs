using Pulaski.Connectors.Sparkplug;

namespace Pulaski.ProcessSimulator;

/// <summary>
/// A Sparkplug B edge node with one device, as messages (no MQTT): its death certificate (the MQTT will), its births, and data, with the
/// sequence numbers (0 to 255 from each birth) and birth/death sequence (<c>bdSeq</c>, one per MQTT session) a host checks.
/// </summary>
public sealed class SparkplugNode(string group, string node, string device, TimeProvider time)
{
    private const string Namespace = "spBv1.0";
    private ulong _bdSeq;
    private ulong _seq;

    public string CommandTopic => $"{Namespace}/{group}/NCMD/{node}";

    /// <summary>A new MQTT session: the next birth/death sequence, and the death a broker sends if the session drops.</summary>
    public (string Topic, byte[] Payload) NewSession(ulong bdSeq)
    {
        _bdSeq = bdSeq;
        return ($"{Namespace}/{group}/NDEATH/{node}", new SparkplugPayload(Now(), null, [SparkplugMetric.Of("bdSeq", _bdSeq)]).Encode());
    }

    /// <summary>The node's and the device's births, with every metric's current value; sequence numbers start again.</summary>
    public IReadOnlyList<(string Topic, byte[] Payload)> Births(IReadOnlyList<(string Metric, double Value)> values)
    {
        _seq = 0;
        var nodeBirth = new SparkplugPayload(Now(), Next(), [SparkplugMetric.Of("bdSeq", _bdSeq), SparkplugMetric.Of("Node Control/Rebirth", false)]);
        var deviceBirth = new SparkplugPayload(Now(), Next(), [.. values.Select(v => SparkplugMetric.Of(v.Metric, v.Value))]);
        return
        [
            ($"{Namespace}/{group}/NBIRTH/{node}", nodeBirth.Encode()),
            ($"{Namespace}/{group}/DBIRTH/{node}/{device}", deviceBirth.Encode()),
        ];
    }

    public (string Topic, byte[] Payload) Data(IReadOnlyList<(string Metric, double Value)> values) =>
        ($"{Namespace}/{group}/DDATA/{node}/{device}", new SparkplugPayload(Now(), Next(), [.. values.Select(v => SparkplugMetric.Of(v.Metric, v.Value))]).Encode());

    /// <summary>Whether a command asks for the births again (Node Control/Rebirth).</summary>
    public static bool AsksForRebirth(ReadOnlySpan<byte> payload)
    {
        try
        {
            return SparkplugPayload.Decode(payload).Metrics.Any(m => m.Name == "Node Control/Rebirth" && m.Value is true);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private ulong Next()
    {
        var seq = _seq;
        _seq = (_seq + 1) % 256;
        return seq;
    }

    private ulong Now() => (ulong)time.GetUtcNow().ToUnixTimeMilliseconds();
}
