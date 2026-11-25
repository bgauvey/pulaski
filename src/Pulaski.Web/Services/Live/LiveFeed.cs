using System.Text.Json;
using Pulaski.Contracts.Live;

namespace Pulaski.Web.Services.Live;

/// <summary>
/// The API's live changes for this connection (#143, D-117): one stream per signed-in circuit, read as the user, opened when the first page
/// subscribes and closed with the circuit. Each subscription says which changes it cares about and how to refresh; a refresh runs at most
/// every <see cref="LiveSubscription.MinimumGap"/>, and a change arriving while one runs brings one more after it. Losing the stream, it tries
/// again with a growing pause, then has every page refresh (what changed meanwhile wasn't heard).
/// </summary>
public sealed partial class LiveFeed(PulaskiApiClient api, TimeProvider time, ILogger<LiveFeed> logger) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _lock = new();
    private readonly List<LiveSubscription> _subscriptions = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _reading;

    /// <summary>Whether the stream is open now; pages may show it.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Calls <paramref name="refresh"/> when a change <paramref name="relevant"/> lets through arrives, until disposed: at most every
    /// <paramref name="gap"/> (default <see cref="LiveSubscription.MinimumGap"/>), for pages whose reload is heavy.
    /// </summary>
    public IDisposable Subscribe(Func<LiveChangeDto, bool> relevant, Func<Task> refresh, TimeSpan? gap = null)
    {
        var subscription = new LiveSubscription(relevant, refresh, time, this, gap ?? LiveSubscription.MinimumGap);
        lock (_lock)
        {
            _subscriptions.Add(subscription);
            _reading ??= Task.Run(() => ReadAsync(_stop.Token));
        }

        return subscription;
    }

    internal void Remove(LiveSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    /// <summary>Hands a batch to every subscription that cares (a resync to all).</summary>
    public void Dispatch(IReadOnlyList<LiveChangeDto> batch)
    {
        LiveSubscription[] current;
        lock (_lock)
        {
            current = [.. _subscriptions];
        }

        foreach (var subscription in current)
        {
            if (batch.Any(c => c.Type == LiveFilters.Resync || subscription.Relevant(c)))
            {
                subscription.Poke();
            }
        }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(2);
        var everHeard = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await api.OpenLiveAsync(ct);
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(body);
                IsConnected = true;
                pause = TimeSpan.FromSeconds(2);
                if (everHeard)
                {
                    Dispatch([new LiveChangeDto(LiveFilters.Resync, LiveFilters.Resync, null, null)]);
                }

                everHeard = true;
                await foreach (var batch in LiveEvents.ReadAsync(reader, ct))
                {
                    Dispatch(batch);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ApiException or JsonException)
            {
                LogLost(logger, ex.Message, pause);
            }

            IsConnected = false;
            try
            {
                await Task.Delay(pause, time, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            pause = TimeSpan.FromSeconds(Math.Min(30, pause.TotalSeconds * 2));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_reading is { } reading)
        {
            try
            {
                await reading;
            }
            catch (OperationCanceledException)
            {
                // Closed.
            }
        }

        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Live updates paused ({Problem}); trying again in {Pause}.")]
    private static partial void LogLost(ILogger logger, string problem, TimeSpan pause);
}

/// <summary>One page's interest in live changes: refreshes at most every <see cref="MinimumGap"/>, never two at once.</summary>
public sealed class LiveSubscription(Func<LiveChangeDto, bool> relevant, Func<Task> refresh, TimeProvider time, LiveFeed feed, TimeSpan gap) : IDisposable
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private bool _running;
    private bool _again;
    private bool _disposed;
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;

    public Func<LiveChangeDto, bool> Relevant { get; } = relevant;

    /// <summary>A change it cares about arrived: refresh now, or once the running one finishes.</summary>
    public void Poke()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_running)
            {
                _again = true;
                return;
            }

            _running = true;
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            var wait = _lastRun + gap - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time);
            }

            try
            {
                _lastRun = time.GetUtcNow();
                await refresh();
            }
            catch (Exception ex) when (ex is ApiException or ObjectDisposedException or InvalidOperationException)
            {
                // The page shows its own problems on its next load, or has gone.
            }

            lock (_lock)
            {
                if (!_again || _disposed)
                {
                    _running = false;
                    return;
                }

                _again = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        feed.Remove(this);
    }
}

/// <summary>Reads server-sent events from the API's live stream: each <c>changes</c> event's batch; comments are skipped (#143).</summary>
public static class LiveEvents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async IAsyncEnumerable<IReadOnlyList<LiveChangeDto>> ReadAsync(
        TextReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? name = null;
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (name == "changes" && data.Length > 0 && JsonSerializer.Deserialize<List<LiveChangeDto>>(data.ToString(), Json) is { } batch)
                {
                    yield return batch;
                }

                (name, _) = (null, data.Clear());
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].TrimStart());
            }
        }
    }
}

/// <summary>Which changes a page cares about (#143). Pure.</summary>
public static class LiveFilters
{
    public const string Resync = "resync";
    public const string Machine = "machine";

    /// <summary>A change of one of <paramref name="kinds"/> at <paramref name="siteId"/> (or with no site known).</summary>
    public static Func<LiveChangeDto, bool> At(Func<Guid?> siteId, params string[] kinds) =>
        change => kinds.Contains(change.Kind) && (change.SiteId is null || siteId() is not { } site || change.SiteId == site);

    /// <summary>A change to one thing: its kind and id.</summary>
    public static Func<LiveChangeDto, bool> To(string kind, Func<Guid> id) => change => change.Kind == kind && change.Id == id();
}
