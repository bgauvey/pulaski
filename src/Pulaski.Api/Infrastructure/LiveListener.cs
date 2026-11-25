using System.Text.Json;
using System.Threading.Channels;
using Npgsql;
using Pulaski.Application.Live;
using Pulaski.Application.Security;
using Pulaski.Contracts.Live;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Listens on the database channel <c>pulaski_live</c> (#143, D-117) over a connection of its own and hands each change, with its site, to the
/// <see cref="LiveHub"/>. Losing the connection, it tries again every few seconds and then tells every screen to read everything again
/// (<c>resync</c>), since what changed meanwhile wasn't heard. <c>Live:Enabled</c> (true) switches it off.
/// </summary>
internal sealed partial class LiveListener(
    NpgsqlDataSource dataSource, ISiteLookup sites, LiveHub hub, IConfiguration configuration, TimeProvider time, Application.Health.ServiceHealth health,
    ILogger<LiveListener> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    // Sites already looked up, by kind and id: a job's or a machine's site never changes. Cleared when it grows large.
    private readonly Dictionary<(string Kind, Guid Id), Guid?> _siteCache = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Live:Enabled", true))
        {
            health.Disabled(Application.Health.BackgroundServices.Live);
            return;
        }

        health.Started(Application.Health.BackgroundServices.Live, null);

        var notices = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var publishing = PublishAsync(notices.Reader, stoppingToken);
        var lost = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, e) => notices.Writer.TryWrite(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {LiveChannel.Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                health.Succeeded(Application.Health.BackgroundServices.Live);
                if (lost)
                {
                    LogBack(logger);
                    hub.Publish(new LiveChangeDto(LiveChannel.Resync, LiveChannel.Resync, null, null));
                    lost = false;
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or IOException)
            {
                lost = true;
                health.Failed(Application.Health.BackgroundServices.Live, ex.Message);
                LogLost(logger, ex, Retry);
                await Task.Delay(Retry, time, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        notices.Writer.TryComplete();
        await publishing;
    }

    private async Task PublishAsync(ChannelReader<string> notices, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var payload in notices.ReadAllAsync(stoppingToken))
            {
                if (Parse(payload) is not { } notice)
                {
                    continue;
                }

                hub.Publish(new LiveChangeDto(notice.Type, notice.Kind, notice.Id, await SiteAsync(notice.Kind, notice.Id, stoppingToken)));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task<Guid?> SiteAsync(string kind, Guid id, CancellationToken cancellationToken)
    {
        if (LiveSites.For(kind) is not { } siteOf)
        {
            return null;
        }

        if (_siteCache.TryGetValue((kind, id), out var cached))
        {
            return cached;
        }

        if (_siteCache.Count > 10_000)
        {
            _siteCache.Clear();
        }

        try
        {
            return _siteCache[(kind, id)] = await sites.SiteOfAsync(siteOf, id, cancellationToken);
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }

    private static Notice? Parse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<Notice>(payload, Json) is { Type.Length: > 0, Kind.Length: > 0 } notice ? notice : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Notice(string Type, string Kind, Guid Id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the live channel; listening again in {Retry}.")]
    private static partial void LogLost(ILogger logger, Exception ex, TimeSpan retry);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening on the live channel again; screens are told to read everything again.")]
    private static partial void LogBack(ILogger logger);
}
