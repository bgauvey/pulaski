using Microsoft.Extensions.Options;
using Pulaski.Connectors;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Edge;

/// <summary>
/// Runs a connector for each active connection of the edge node (D-050), with the configuration from Pulaski, or the last one
/// kept in the buffer while Pulaski can't be reached. Refreshes the configuration regularly: a connection whose settings changed
/// is restarted, a new tag is read from then on. Also reports the addresses its connections have seen.
/// </summary>
public sealed class ConnectorHost(
    PulaskiClient api, EdgeBuffer buffer, TagBinder binder, IEnumerable<IConnectorFactory> factories, IOptions<EdgeOptions> options,
    ILoggerFactory loggers, TimeProvider time, ILogger<ConnectorHost> log) : BackgroundService
{
    private readonly Dictionary<Guid, Running> _running = [];
    private int _seenFailures;

    private sealed record Running(string Fingerprint, IConnector Connector, CancellationTokenSource Stop, Task Task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.EdgeNodeId == Guid.Empty)
        {
            log.LogError("Set Edge:EdgeNodeId to the edge node's id from Pulaski (Configure → Edge nodes).");
            return;
        }

        var lastRefresh = DateTimeOffset.MinValue;
        var lastMetrics = time.GetUtcNow();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                if (now - lastRefresh >= options.Value.ConfigRefresh)
                {
                    lastRefresh = now;
                    if (await ConfigAsync(stoppingToken) is { } config)
                    {
                        await ApplyAsync(config);
                    }
                }

                binder.Flush(now);
                if (now - lastMetrics >= options.Value.MetricsInterval)
                {
                    lastMetrics = now;
                    await ReportSeenAsync(stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var id in _running.Keys.ToList())
            {
                await StopAsync(id);
            }
        }
    }

    private async Task<EdgeConfigDto?> ConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await api.GetConfigAsync(cancellationToken);
            buffer.SaveConfig(config);
            return config;
        }
        catch (Exception ex) when (ex is HttpRequestException or PulaskiApiException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var kept = _running.Count == 0 ? buffer.LoadConfig() : null;
            log.LogWarning("Can't read the configuration from Pulaski ({Message}){Fallback}", ex.Message,
                kept is not null ? "; running with the last one kept" : _running.Count > 0 ? "; carrying on as configured" : "; nothing to run yet");
            return kept;
        }
    }

    private async Task ApplyAsync(EdgeConfigDto config)
    {
        binder.Use(config);
        var wanted = config.IsActive ? config.Connections.Where(c => c.IsActive).ToDictionary(c => c.Id) : [];
        if (!config.IsActive)
        {
            log.LogWarning("Edge node {Code} is switched off in Pulaski; not collecting.", config.Code);
        }

        foreach (var id in _running.Keys.Where(id => !wanted.TryGetValue(id, out var c) || Fingerprint(c) != _running[id].Fingerprint).ToList())
        {
            await StopAsync(id);
        }

        foreach (var connection in wanted.Values.Where(c => !_running.ContainsKey(c.Id)))
        {
            var factory = factories.FirstOrDefault(f => f.Protocol == connection.Protocol);
            if (factory is null)
            {
                log.LogError("No connector for {Protocol}; connection {Code} isn't read.", connection.Protocol, connection.Code);
                continue;
            }

            var secrets = options.Value.Connections.GetValueOrDefault(connection.Code) ?? [];
            IConnector connector;
            try
            {
                connector = factory.Create(new ConnectorSettings(connection.Code, connection.Settings, secrets), loggers);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                log.LogError("Connection {Code} can't start: {Message}", connection.Code, ex.Message);
                continue;
            }

            connector.Watch(binder.AddressesOf(connection.Id));
#pragma warning disable CA2000 // Kept in _running and disposed by StopAsync.
            var stop = new CancellationTokenSource();
#pragma warning restore CA2000
            var sink = new Sink(connection.Id, binder);
            var task = Task.Run(() => connector.RunAsync(sink, stop.Token));
            _running[connection.Id] = new Running(Fingerprint(connection), connector, stop, task);
            log.LogInformation("Started connection {Code} ({Protocol})", connection.Code, connection.Protocol);
        }

        foreach (var (id, running) in _running)
        {
            running.Connector.Watch(binder.AddressesOf(id));
        }
    }

    private async Task StopAsync(Guid id)
    {
        var running = _running[id];
        _running.Remove(id);
        await running.Stop.CancelAsync();
        try
        {
            await running.Task;
        }
        catch (OperationCanceledException)
        {
        }

        await running.Connector.DisposeAsync();
        running.Stop.Dispose();
    }

    private async Task ReportSeenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReportSeenAsync(binder, api.SendMetricsAsync, cancellationToken);
            _seenFailures = 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or PulaskiApiException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Kept to try again; a failure that goes on is a warning, so a catalog that stays out of date shows in the log.
            _seenFailures++;
            log.Log(_seenFailures is 1 or 3 || _seenFailures % 10 == 0 ? LogLevel.Warning : LogLevel.Debug,
                "Couldn't report the addresses seen ({Failures} times in a row); trying again: {Message}", _seenFailures, ex.Message);
        }
    }

    /// <summary>
    /// Reports the addresses seen since the last report. When that fails they are put back and go with the next report (#11), so an
    /// address seen only once (a Sparkplug birth during an outage) still reaches Pulaski's catalog. Reporting one again is harmless.
    /// </summary>
    internal static async Task ReportSeenAsync(
        TagBinder binder, Func<EdgeMetricsRequest, CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        var seen = binder.TakeSeen();
        if (seen.Count == 0)
        {
            return;
        }

        try
        {
            await send(new EdgeMetricsRequest { Metrics = seen }, cancellationToken);
        }
        catch
        {
            binder.ReturnSeen(seen);
            throw;
        }
    }

    private static string Fingerprint(EdgeConnectionDto connection) =>
        connection.Protocol + "|" + string.Join("|", connection.Settings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}"));

    private sealed class Sink(Guid connectionId, TagBinder binder) : ISampleSink
    {
        public ValueTask PublishAsync(ConnectorSample sample, CancellationToken cancellationToken)
        {
            binder.Accept(connectionId, sample);
            return ValueTask.CompletedTask;
        }

        public ValueTask DiscoveredAsync(DiscoveredAddress address, CancellationToken cancellationToken)
        {
            binder.Discovered(connectionId, address);
            return ValueTask.CompletedTask;
        }
    }
}
