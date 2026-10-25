using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Pulaski.Connectors.EthernetIp;

public sealed class EthernetIpConnectorFactory(TimeProvider time) : IConnectorFactory
{
    public string Protocol => "ethernet_ip";

    public IConnector Create(ConnectorSettings settings, ILoggerFactory loggers)
    {
        var options = EthernetIpOptions.From(settings);
#pragma warning disable CA2000 // The connector owns the reader and disposes it with itself.
        return new EthernetIpConnector(options, new LibplctagReader(options), time, loggers.CreateLogger<EthernetIpConnector>());
#pragma warning restore CA2000
    }
}

/// <summary>An EtherNet/IP connection's settings (#24): the PLC's address, the CIP path to its CPU, its family, and how often to read.</summary>
public sealed record EthernetIpOptions(string ConnectionCode, string Gateway, string? Path, string Plc, TimeSpan PollInterval, TimeSpan Timeout)
{
    public static EthernetIpOptions From(ConnectorSettings settings) => new(
        settings.ConnectionCode,
        settings.Get("gateway") ?? throw new InvalidOperationException($"Connection {settings.ConnectionCode} has no gateway (the PLC's address)."),
        settings.Get("path"),
        settings.Get("plc") ?? "controllogix",
        TimeSpan.FromMilliseconds(Milliseconds(settings.Get("pollIntervalMs"), 1000)),
        TimeSpan.FromMilliseconds(Milliseconds(settings.Get("timeoutMs"), 5000)));

    private static int Milliseconds(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0 ? ms : fallback;
}

/// <summary>Reads one CIP tag at a time; a reading that fails throws. Kept apart from the polling so it can be tested without a PLC.</summary>
public interface ICipTagReader : IAsyncDisposable
{
    /// <summary>The tag's current value: a bool, a whole number or a floating-point number, by its type.</summary>
    Task<object> ReadAsync(EthernetIpAddress address, CancellationToken cancellationToken);

    /// <summary>Drops what is kept for the address (its connection handle), so the next read starts afresh.</summary>
    ValueTask ForgetAsync(string address);
}

/// <summary>
/// An EtherNet/IP connector (#24): reads the bound tags from the PLC every poll interval and publishes each as a sample, stamped with
/// when it was read (CIP reads carry no source time). The edge forwards only changes, so publishing every read costs nothing upstream.
/// A tag that can't be read is published once as Bad and retried each poll; one that comes back is published again. An address is
/// announced (discovered) the first time it reads, with its type, so it shows on the edge node's Seen tab.
/// </summary>
public sealed class EthernetIpConnector(EthernetIpOptions options, ICipTagReader reader, TimeProvider time, ILogger<EthernetIpConnector> log) : IConnector
{
    private readonly Lock _lock = new();
    private IReadOnlySet<string> _watched = new HashSet<string>();
    private readonly HashSet<string> _failing = [];
    private readonly HashSet<string> _announced = [];

    public void Watch(IReadOnlySet<string> addresses)
    {
        lock (_lock)
        {
            _watched = addresses;
        }
    }

    public async Task RunAsync(ISampleSink sink, CancellationToken cancellationToken)
    {
        log.LogInformation("Reading {Gateway} ({Plc}) every {Interval} ms for {Code}", options.Gateway, options.Plc, options.PollInterval.TotalMilliseconds, options.ConnectionCode);
        using var timer = new PeriodicTimer(options.PollInterval, time);
        do
        {
            await PollAsync(sink, cancellationToken);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    /// <summary>One round: every watched address read once, in name order.</summary>
    internal async Task PollAsync(ISampleSink sink, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> watched;
        lock (_lock)
        {
            watched = _watched;
        }

        foreach (var address in watched.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EthernetIpAddress parsed;
            try
            {
                parsed = EthernetIpAddress.Parse(address);
            }
            catch (FormatException ex)
            {
                await FailAsync(sink, address, ex.Message, cancellationToken);
                continue;
            }

            try
            {
                var value = await reader.ReadAsync(parsed, cancellationToken);
                var at = time.GetUtcNow();
                if (_failing.Remove(address))
                {
                    log.LogInformation("{Code}: {Address} reads again", options.ConnectionCode, address);
                }

                if (_announced.Add(address))
                {
                    await sink.DiscoveredAsync(new DiscoveredAddress(address, parsed.DataType, value, at), cancellationToken);
                }

                await sink.PublishAsync(new ConnectorSample(address, value, at), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A fresh handle next time: the PLC may have restarted or the connection dropped.
                await reader.ForgetAsync(address);
                await FailAsync(sink, address, ex.Message, cancellationToken);
            }
        }
    }

    private async Task FailAsync(ISampleSink sink, string address, string why, CancellationToken cancellationToken)
    {
        if (_failing.Add(address))
        {
            log.LogWarning("{Code}: can't read {Address} from {Gateway}: {Why}", options.ConnectionCode, address, options.Gateway, why);
            await sink.PublishAsync(new ConnectorSample(address, null, time.GetUtcNow(), SampleQuality.Bad), cancellationToken);
        }
    }

    public ValueTask DisposeAsync() => reader.DisposeAsync();
}
