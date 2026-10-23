namespace Pulaski.Connectors.Sparkplug;

/// <summary>A Sparkplug B topic: <c>spBv1.0/group/TYPE/node[/device]</c>.</summary>
public sealed record SparkplugTopic(string Group, string MessageType, string Node, string? Device)
{
    public const string Namespace = "spBv1.0";

    public static SparkplugTopic? Parse(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length is 4 or 5 && parts[0] == Namespace && parts[1] != "STATE"
            ? new SparkplugTopic(parts[1], parts[2], parts[3], parts.Length == 5 ? parts[4] : null)
            : null;
    }

    /// <summary>The topic a command to an edge node goes to.</summary>
    public static string NodeCommand(string group, string node) => $"{Namespace}/{group}/NCMD/{node}";

    /// <summary>Where a metric is addressed in Pulaski: <c>group/node[/device]:metric</c>.</summary>
    public string Address(string metric) => $"{Group}/{Node}{(Device is null ? "" : "/" + Device)}:{metric}";
}

/// <summary>What the host makes of one message: samples, metrics seen, and whether to ask the node to send its birth again.</summary>
public sealed record SparkplugOutcome(IReadOnlyList<ConnectorSample> Samples, IReadOnlyList<DiscoveredAddress> Discovered, (string Group, string Node)? Rebirth);

/// <summary>
/// The Sparkplug B host application's view of the edge nodes it hears (D-050), without MQTT: births name metrics and their aliases,
/// data refers to them by alias, deaths mean no signal. A node whose birth Pulaski missed, or whose sequence numbers skip, is asked
/// to send its birth again (a rebirth), since its aliases can't be trusted until then.
/// </summary>
public sealed class SparkplugHost(TimeProvider time)
{
    private static readonly TimeSpan RebirthInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<(string Group, string Node), NodeState> _nodes = [];

    private sealed class NodeState
    {
        public bool Born { get; set; }
        public ulong? LastSequence { get; set; }
        public DateTimeOffset? RebirthAsked { get; set; }

        /// <summary>Per device (null for the node itself): alias to metric name, and the addresses last reported.</summary>
        public Dictionary<string, Dictionary<ulong, string>> Aliases { get; } = [];
        public Dictionary<string, HashSet<string>> Addresses { get; } = [];
    }

    public SparkplugOutcome Handle(string topicText, ReadOnlySpan<byte> payloadBytes)
    {
        var topic = SparkplugTopic.Parse(topicText);
        if (topic is null || topic.MessageType is "NCMD" or "DCMD")
        {
            return Empty;
        }

        SparkplugPayload payload;
        try
        {
            payload = SparkplugPayload.Decode(payloadBytes);
        }
        catch (FormatException)
        {
            return Empty;
        }

        var key = (topic.Group, topic.Node);
        if (!_nodes.TryGetValue(key, out var node))
        {
            node = new NodeState();
            _nodes[key] = node;
        }

        var now = time.GetUtcNow();
        var deviceKey = topic.Device ?? "";
        switch (topic.MessageType)
        {
            case "NBIRTH":
                node.Born = true;
                node.LastSequence = payload.Sequence;
                node.Aliases.Clear();
                return Birth(topic, node, deviceKey, payload, now);
            case "DBIRTH":
                if (!Continues(node, payload))
                {
                    return Rebirth(topic, node, now);
                }

                return Birth(topic, node, deviceKey, payload, now);
            case "NDATA" or "DDATA":
                if (!node.Born || !Continues(node, payload))
                {
                    return Rebirth(topic, node, now);
                }

                return Data(topic, node, deviceKey, payload, now);
            case "DDEATH":
                Continues(node, payload);
                return new SparkplugOutcome(Lost(node, deviceKey, now), [], null);
            case "NDEATH":
                node.Born = false;
                node.LastSequence = null;
                return new SparkplugOutcome([.. node.Addresses.Keys.ToList().SelectMany(d => Lost(node, d, now))], [], null);
            default:
                return Empty;
        }
    }

    /// <summary>Every address heard, as lost: the broker connection dropped.</summary>
    public IReadOnlyList<ConnectorSample> LoseAll()
    {
        var now = time.GetUtcNow();
        var lost = _nodes.Values.SelectMany(n => n.Addresses.Keys.ToList().SelectMany(d => Lost(n, d, now))).ToList();
        _nodes.Clear();
        return lost;
    }

    private static SparkplugOutcome Empty { get; } = new([], [], null);

    private static SparkplugOutcome Birth(SparkplugTopic topic, NodeState node, string deviceKey, SparkplugPayload payload, DateTimeOffset now)
    {
        var aliases = new Dictionary<ulong, string>();
        node.Aliases[deviceKey] = aliases;
        var addresses = node.Addresses.TryGetValue(deviceKey, out var known) ? known : node.Addresses[deviceKey] = [];
        var samples = new List<ConnectorSample>();
        var discovered = new List<DiscoveredAddress>();
        foreach (var metric in payload.Metrics.Where(m => m.Name is not null))
        {
            if (metric.Alias is { } alias)
            {
                aliases[alias] = metric.Name!;
            }

            var address = topic.Address(metric.Name!);
            var at = At(metric.Timestamp ?? payload.Timestamp, now);
            addresses.Add(address);
            discovered.Add(new DiscoveredAddress(address, metric.DataType.ToString(), metric.Value, at));
            samples.Add(new ConnectorSample(address, metric.Value, at, metric.IsNull ? SampleQuality.Bad : SampleQuality.Good));
        }

        return new SparkplugOutcome(samples, discovered, null);
    }

    private static SparkplugOutcome Data(SparkplugTopic topic, NodeState node, string deviceKey, SparkplugPayload payload, DateTimeOffset now)
    {
        var aliases = node.Aliases.GetValueOrDefault(deviceKey);
        var samples = new List<ConnectorSample>();
        foreach (var metric in payload.Metrics)
        {
            var name = metric.Name ?? (metric.Alias is { } alias && aliases is not null && aliases.TryGetValue(alias, out var n) ? n : null);
            if (name is null)
            {
                // An alias the birth didn't name: the birth was missed or is stale.
                return Rebirth(topic, node, now);
            }

            samples.Add(new ConnectorSample(topic.Address(name), metric.Value, At(metric.Timestamp ?? payload.Timestamp, now),
                metric.IsNull ? SampleQuality.Bad : SampleQuality.Good));
        }

        return new SparkplugOutcome(samples, [], null);
    }

    /// <summary>Sequence numbers count 0 to 255 per node across everything it sends after its birth.</summary>
    private static bool Continues(NodeState node, SparkplugPayload payload)
    {
        if (payload.Sequence is not { } sequence || node.LastSequence is not { } last)
        {
            node.LastSequence = payload.Sequence;
            return node.Born;
        }

        node.LastSequence = sequence;
        return sequence == (last + 1) % 256;
    }

    private static SparkplugOutcome Rebirth(SparkplugTopic topic, NodeState node, DateTimeOffset now)
    {
        node.Born = false;
        if (node.RebirthAsked is { } asked && now - asked < RebirthInterval)
        {
            return Empty;
        }

        node.RebirthAsked = now;
        return new SparkplugOutcome([], [], (topic.Group, topic.Node));
    }

    private static List<ConnectorSample> Lost(NodeState node, string deviceKey, DateTimeOffset now) =>
        node.Addresses.TryGetValue(deviceKey, out var addresses)
            ? [.. addresses.Select(a => new ConnectorSample(a, null, now, SampleQuality.Bad))]
            : [];

    private static DateTimeOffset At(ulong? millis, DateTimeOffset now) =>
        millis is { } ms && ms > 0 && ms < 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : now;

    /// <summary>The command asking a node for its birth again (Node Control/Rebirth).</summary>
    public byte[] RebirthCommand() =>
        new SparkplugPayload((ulong)time.GetUtcNow().ToUnixTimeMilliseconds(), null, [SparkplugMetric.Of("Node Control/Rebirth", true)]).Encode();
}
