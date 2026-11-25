using System.Collections.Concurrent;
using System.Threading.Channels;
using Pulaski.Application.Security;
using Pulaski.Contracts.Live;

namespace Pulaski.Application.Live;

/// <summary>
/// What tells open screens that something changed (#143, D-117): every event appended (<see cref="Abstractions.IEventLog"/>) and every machine
/// ingest touched notifies the database channel <see cref="Channel"/> on commit; the API listens and fans the changes out here.
/// </summary>
public static class LiveChannel
{
    public const string Channel = "pulaski_live";

    /// <summary>The kind of a machine's change from ingest (state, stop, count, alarm); its id is the equipment's.</summary>
    public const string Machine = "machine";

    /// <summary>The listener lost the channel and came back: everything may have changed.</summary>
    public const string Resync = "resync";
}

/// <summary>Machines changed by ingest, notified in its transaction so screens hear of it once it commits (#143).</summary>
public interface ILiveSignals
{
    Task MachinesChangedAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);
}

/// <summary>
/// The site a change belongs to, from the subject type its event names (#143): the same lookups the permission checks use. Null: a change
/// with no site (or one Pulaski doesn't map), sent to everyone; screens decide what they care about. Pure.
/// </summary>
public static class LiveSites
{
    private static readonly Dictionary<string, SiteOf> Kinds = new(StringComparer.Ordinal)
    {
        ["equipment"] = SiteOf.Equipment,
        [LiveChannel.Machine] = SiteOf.Equipment,
        ["job"] = SiteOf.Job,
        ["work_order"] = SiteOf.WorkOrder,
        ["lot"] = SiteOf.Lot,
        ["container"] = SiteOf.Container,
        ["stock_count"] = SiteOf.StockCount,
        ["partial_batch"] = SiteOf.PartialBatch,
        ["run_standard"] = SiteOf.RunStandard,
        ["person"] = SiteOf.Person,
        ["labor_session"] = SiteOf.LaborSession,
        ["labor_break"] = SiteOf.LaborBreak,
        ["andon_call"] = SiteOf.AndonCall,
        ["maintenance_request"] = SiteOf.MaintenanceRequest,
        ["handover_entry"] = SiteOf.HandoverEntry,
        ["feed_shortfall"] = SiteOf.FeedShortfall,
        ["expected_receipt"] = SiteOf.ExpectedReceipt,
    };

    public static SiteOf? For(string kind) => Kinds.TryGetValue(kind, out var siteOf) ? siteOf : null;

    /// <summary>Whether a reader who sees <paramref name="visibleSites"/> (null: every site) hears of <paramref name="change"/>.</summary>
    public static bool Visible(LiveChangeDto change, IReadOnlyCollection<Guid>? visibleSites) =>
        visibleSites is null || change.SiteId is not { } site || visibleSites.Contains(site);
}

/// <summary>
/// Hands every change to every open stream (#143): each subscriber has its own bounded queue that drops its oldest when a reader falls behind
/// (a screen then refreshes anyway). In memory, per API process.
/// </summary>
public sealed class LiveHub
{
    public const int QueueLength = 512;

    private readonly ConcurrentDictionary<Guid, Channel<LiveChangeDto>> _subscribers = new();

    public int Subscribers => _subscribers.Count;

    public (Guid Id, ChannelReader<LiveChangeDto> Changes) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<LiveChangeDto>(new BoundedChannelOptions(QueueLength)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Publish(LiveChangeDto change)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(change);
        }
    }

    /// <summary>
    /// Waits for the next change, then gathers what else arrives within <paramref name="gather"/>, so a burst (a report and its lots) goes as
    /// one message; repeats of the same change are sent once. Empty when <paramref name="wait"/> passes with nothing.
    /// </summary>
    public static async Task<IReadOnlyList<LiveChangeDto>> NextBatchAsync(
        ChannelReader<LiveChangeDto> changes, TimeSpan wait, TimeSpan gather, TimeProvider time, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(wait);
        try
        {
            if (!await changes.WaitToReadAsync(waiting.Token))
            {
                return [];
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        await Task.Delay(gather, time, cancellationToken);
        var batch = new List<LiveChangeDto>();
        while (changes.TryRead(out var change))
        {
            if (!batch.Contains(change))
            {
                batch.Add(change);
            }
        }

        return batch;
    }
}
