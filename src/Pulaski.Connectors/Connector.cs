using Microsoft.Extensions.Logging;

namespace Pulaski.Connectors;

/// <summary>Whether a sample can be trusted (D-050). <see cref="Bad"/> means no signal: the source went away or reports the value as bad.</summary>
public enum SampleQuality
{
    Good,
    Bad,
}

/// <summary>
/// One value a connector read (D-050). The address is the connector's own (for Sparkplug B, <c>group/node[/device]:metric</c>);
/// the value is a number, boolean or string as the source sent it, null when it has none.
/// </summary>
public sealed record ConnectorSample(string Address, object? Value, DateTimeOffset Timestamp, SampleQuality Quality = SampleQuality.Good);

/// <summary>An address a connector has seen, whether or not anything is bound to it, so it can be offered for binding.</summary>
public sealed record DiscoveredAddress(string Address, string? DataType, object? Value, DateTimeOffset SeenAt);

/// <summary>Where a connector puts what it reads. Implemented by the edge host; called from the connector's own threads.</summary>
public interface ISampleSink
{
    /// <summary>A value for an address someone is interested in (or any address; the host drops what isn't bound).</summary>
    ValueTask PublishAsync(ConnectorSample sample, CancellationToken cancellationToken);

    /// <summary>An address the source described, such as a metric in a Sparkplug birth certificate.</summary>
    ValueTask DiscoveredAsync(DiscoveredAddress address, CancellationToken cancellationToken);
}

/// <summary>A connection's settings as Pulaski keeps them, plus the secrets from the edge's own configuration.</summary>
public sealed record ConnectorSettings(string ConnectionCode, IReadOnlyDictionary<string, string> Settings, IReadOnlyDictionary<string, string> Secrets)
{
    public string? Get(string key) => Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public string? Secret(string key) => Secrets.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>
/// A connector (D-050): connects to one data source and publishes samples until stopped. It knows nothing about PackML,
/// equipment or OEE; the edge host binds addresses to equipment. A connector reconnects by itself when its source drops,
/// and reports the addresses it lost as <see cref="SampleQuality.Bad"/>.
/// </summary>
public interface IConnector : IAsyncDisposable
{
    /// <summary>Connects and keeps the connection until <paramref name="cancellationToken"/> is cancelled.</summary>
    Task RunAsync(ISampleSink sink, CancellationToken cancellationToken);

    /// <summary>The addresses the host has bound, so a connector that subscribes per address knows which; may change while running.</summary>
    void Watch(IReadOnlySet<string> addresses);
}

/// <summary>Makes connectors of one protocol. The edge host picks the factory by a connection's protocol.</summary>
public interface IConnectorFactory
{
    /// <summary>The protocol, as Pulaski names it, such as <c>sparkplug_b</c>.</summary>
    string Protocol { get; }

    IConnector Create(ConnectorSettings settings, ILoggerFactory loggers);
}
