using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Pulaski.Connectors.OpcUa;

/// <summary>Makes OPC UA connectors; <paramref name="pkiRoot"/> is where the edge keeps its certificate and trust lists.</summary>
public sealed class OpcUaConnectorFactory(TimeProvider time, string pkiRoot) : IConnectorFactory
{
    public string Protocol => "opc_ua";

    public IConnector Create(ConnectorSettings settings, ILoggerFactory loggers)
    {
        var options = OpcUaOptions.From(settings, pkiRoot);
        return new OpcUaConnector(options, () => new OpcFoundationSession(options, loggers), time, loggers.CreateLogger<OpcUaConnector>());
    }
}

/// <summary>
/// An OPC UA connection's settings (#22). Secure by default: sign and encrypt, and a server trusted only when its certificate is in the
/// edge's trusted store or matches <see cref="ServerThumbprint"/>; <see cref="TrustAnyServer"/> and security mode none are for simulators.
/// </summary>
public sealed record OpcUaOptions(
    string ConnectionCode, Uri Endpoint, string SecurityMode, string? SecurityPolicy, string? ServerThumbprint, bool TrustAnyServer,
    string? Username, string? Password, TimeSpan PublishingInterval, TimeSpan SamplingInterval, string? BrowseRoot, int BrowseDepth, string PkiRoot)
{
    public static OpcUaOptions From(ConnectorSettings settings, string pkiRoot)
    {
        var endpoint = settings.Get("endpoint") ?? throw new InvalidOperationException($"Connection {settings.ConnectionCode} has no endpoint.");
        var publishing = TimeSpan.FromMilliseconds(Whole(settings.Get("publishingIntervalMs"), 1000));
        return new OpcUaOptions(
            settings.ConnectionCode,
            new Uri(endpoint),
            settings.Get("securityMode") ?? "signAndEncrypt",
            settings.Get("securityPolicy"),
            settings.Get("serverThumbprint")?.ToUpperInvariant(),
            settings.Get("trustAnyServer") == "true",
            settings.Get("username"),
            settings.Secret("password"),
            publishing,
            settings.Get("samplingIntervalMs") is { } sampling ? TimeSpan.FromMilliseconds(Whole(sampling, 0)) : publishing,
            settings.Get("browseRoot"),
            Whole(settings.Get("browseDepth"), 3),
            pkiRoot);
    }

    private static int Whole(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : fallback;
}

/// <summary>A variable a server offers, as browsing found it.</summary>
public sealed record OpcUaNode(string NodeId, string? DataType, object? Value);

/// <summary>A data change of a monitored node: its value, whether its status is good, and the times the server gave.</summary>
public sealed record OpcUaValue(string NodeId, object? Value, bool Good, DateTimeOffset? SourceTimestamp, DateTimeOffset? ServerTimestamp);

/// <summary>One session with an OPC UA server, kept apart from the connector's own logic so that logic can be tested without a server.</summary>
public interface IOpcUaSession : IAsyncDisposable
{
    /// <summary>Data changes of the monitored nodes, from the session's own threads.</summary>
    event Action<OpcUaValue>? ValueChanged;

    /// <summary>The session stopped answering (keep-alives failed) and won't recover by itself.</summary>
    event Action<string>? ConnectionLost;

    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>The variables below <paramref name="root"/> (the Objects folder when none) down to <paramref name="depth"/> levels.</summary>
    Task<IReadOnlyList<OpcUaNode>> BrowseAsync(string? root, int depth, CancellationToken cancellationToken);

    /// <summary>Monitors exactly these nodes: new ones are added, ones no longer wanted removed.</summary>
    Task MonitorAsync(IReadOnlySet<string> nodeIds, CancellationToken cancellationToken);
}

/// <summary>
/// An OPC UA connector (#22): keeps a session with the server, monitors the bound nodes in one subscription and publishes each data
/// change as a sample, at the server's source time (else its server time). A status that isn't good is a Bad sample. Browsing on
/// connect announces the variables it finds, for binding. When the session is lost, every watched address is published as Bad and
/// the connector reconnects, waiting longer each time up to a minute.
/// </summary>
public sealed class OpcUaConnector(OpcUaOptions options, Func<IOpcUaSession> newSession, TimeProvider time, ILogger<OpcUaConnector> log) : IConnector
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private IReadOnlySet<string> _watched = new HashSet<string>();
    private readonly Channel<bool> _watchChanged = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Watch(IReadOnlySet<string> addresses)
    {
        lock (_lock)
        {
            _watched = addresses;
        }

        _watchChanged.Writer.TryWrite(true);
    }

    public async Task RunAsync(ISampleSink sink, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            await using (var session = newSession())
            {
                try
                {
                    await session.ConnectAsync(cancellationToken);
                    connected = true;
                    backoff = TimeSpan.FromSeconds(1);
                    log.LogInformation("Connected to {Endpoint} for {Code}", options.Endpoint, options.ConnectionCode);
                    await RunSessionAsync(session, sink, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log.LogWarning("{Code}: {What} {Endpoint}: {Message}", options.ConnectionCode, connected ? "lost" : "can't connect to", options.Endpoint, ex.Message);
                }
            }

            await LostAsync(sink, cancellationToken);
            await Task.Delay(backoff, time, cancellationToken);
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    // Until the session is lost: publishes data changes and follows changes to what is watched.
    private async Task RunSessionAsync(IOpcUaSession session, ISampleSink sink, CancellationToken cancellationToken)
    {
        var values = Channel.CreateUnbounded<OpcUaValue>(new UnboundedChannelOptions { SingleReader = true });
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ValueChanged += value => values.Writer.TryWrite(value);
        session.ConnectionLost += why => lost.TrySetResult(why);

        await AnnounceAsync(session, sink, cancellationToken);
        // What was watched before connecting is monitored now: a change signalled meanwhile is already in it.
        _watchChanged.Reader.TryRead(out _);
        await session.MonitorAsync(Watched(), cancellationToken);
        while (true)
        {
            // WhenAny doesn't throw for a cancelled wait, so stopping is checked each time round.
            cancellationToken.ThrowIfCancellationRequested();
            var next = values.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var watch = _watchChanged.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var done = await Task.WhenAny(next, watch, lost.Task);
            if (done == lost.Task)
            {
                throw new IOException(await lost.Task);
            }

            while (values.Reader.TryRead(out var value))
            {
                await sink.PublishAsync(Sample(value), cancellationToken);
            }

            if (_watchChanged.Reader.TryRead(out _))
            {
                await session.MonitorAsync(Watched(), cancellationToken);
            }
        }
    }

    private async Task AnnounceAsync(IOpcUaSession session, ISampleSink sink, CancellationToken cancellationToken)
    {
        try
        {
            var now = time.GetUtcNow();
            foreach (var node in await session.BrowseAsync(options.BrowseRoot, options.BrowseDepth, cancellationToken))
            {
                await sink.DiscoveredAsync(new DiscoveredAddress(node.NodeId, node.DataType, node.Value, now), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Browsing only helps binding; reading goes on without it.
            log.LogWarning("{Code}: browsing {Endpoint} failed: {Message}", options.ConnectionCode, options.Endpoint, ex.Message);
        }
    }

    private async Task LostAsync(ISampleSink sink, CancellationToken cancellationToken)
    {
        var at = time.GetUtcNow();
        foreach (var address in Watched().Order(StringComparer.Ordinal))
        {
            await sink.PublishAsync(new ConnectorSample(address, null, at, SampleQuality.Bad), cancellationToken);
        }
    }

    private IReadOnlySet<string> Watched()
    {
        lock (_lock)
        {
            return _watched;
        }
    }

    /// <summary>A data change as a sample: at its source time, else its server time, else now; a status that isn't good is Bad.</summary>
    internal ConnectorSample Sample(OpcUaValue value) => new(
        value.NodeId,
        value.Good ? value.Value : null,
        value.SourceTimestamp ?? value.ServerTimestamp ?? time.GetUtcNow(),
        value.Good ? SampleQuality.Good : SampleQuality.Bad);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
