using Microsoft.Extensions.Options;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Edge;

/// <summary>
/// Forwards the buffer to Pulaski in order (D-050), a batch at a time, and drops a batch once Pulaski has applied it. While
/// Pulaski can't be reached the buffer just grows; sending a batch again is harmless, since Pulaski skips what it applied.
/// </summary>
public sealed class Forwarder(PulaskiClient api, EdgeBuffer buffer, IOptions<EdgeOptions> options, TimeProvider time, ILogger<Forwarder> log) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.EdgeNodeId == Guid.Empty)
        {
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        var failing = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = buffer.Take(options.Value.BatchSize);
                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
                    continue;
                }

                try
                {
                    var result = await api.SendSignalsAsync(
                        new EdgeSignalBatchRequest { StreamId = buffer.StreamId, AgentVersion = PulaskiClient.AgentVersion, Signals = batch }, stoppingToken);
                    buffer.Remove(Math.Max(result.LastSequence, batch[^1].Sequence));
                    if (failing)
                    {
                        log.LogInformation("Pulaski is back; forwarding the buffer ({Count} waiting)", buffer.Count);
                    }

                    (failing, backoff) = (false, TimeSpan.FromSeconds(1));
                }
                catch (Exception ex) when (ex is HttpRequestException or PulaskiApiException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    if (!failing)
                    {
                        log.LogWarning("Can't forward to Pulaski ({Message}); buffering, {Count} waiting", ex.Message, buffer.Count);
                    }

                    failing = true;
                    await Task.Delay(backoff, time, stoppingToken);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
