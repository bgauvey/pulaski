using Microsoft.Extensions.Time.Testing;
using Pulaski.Application.Live;
using Pulaski.Contracts.Live;

namespace Pulaski.Application.Tests.Live;

/// <summary>Changes reach every open stream, in batches, once each, and only where the reader sees the site (#143).</summary>
public class LiveHubTests
{
    private static readonly Guid SiteA = Guid.CreateVersion7(), SiteB = Guid.CreateVersion7();

    [Fact]
    public async Task Every_subscriber_gets_each_change_once_in_a_batch()
    {
        var hub = new LiveHub();
        var (first, a) = hub.Subscribe();
        var (_, b) = hub.Subscribe();
        var job = new LiveChangeDto("pulaski.job.started.v1", "job", Guid.CreateVersion7(), SiteA);
        hub.Publish(job);
        hub.Publish(job);
        hub.Publish(new LiveChangeDto("machine", "machine", Guid.CreateVersion7(), SiteA));

        var batch = await LiveHub.NextBatchAsync(a, TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        Assert.Equal(2, batch.Count);
        Assert.Equal(2, (await LiveHub.NextBatchAsync(b, TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeProvider.System, CancellationToken.None)).Count);

        hub.Unsubscribe(first);
        Assert.Equal(1, hub.Subscribers);
    }

    [Fact]
    public async Task Nothing_within_the_wait_is_an_empty_batch_for_a_heartbeat()
    {
        var hub = new LiveHub();
        var (_, changes) = hub.Subscribe();
        var waiting = LiveHub.NextBatchAsync(changes, TimeSpan.FromMilliseconds(50), TimeSpan.Zero, new FakeTimeProvider(), CancellationToken.None);
        Assert.Empty(await waiting);
    }

    [Fact]
    public void A_reader_hears_of_its_sites_and_of_changes_with_no_site()
    {
        LiveChangeDto atA = new("t", "job", Guid.CreateVersion7(), SiteA), atB = atA with { SiteId = SiteB }, nowhere = atA with { SiteId = null };
        Assert.True(LiveSites.Visible(atA, [SiteA]));
        Assert.False(LiveSites.Visible(atB, [SiteA]));
        Assert.True(LiveSites.Visible(nowhere, [SiteA]));
        Assert.True(LiveSites.Visible(atB, null));
        Assert.Equal(Pulaski.Application.Security.SiteOf.Equipment, LiveSites.For("machine"));
        Assert.Null(LiveSites.For("language"));
    }
}
