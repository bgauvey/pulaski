using Pulaski.Contracts.Live;
using Pulaski.Web.Services.Live;

namespace Pulaski.Web.Tests;

/// <summary>The web reads the API's live stream and refreshes pages that care, never two at once (#143).</summary>
public class LiveFeedTests
{
    [Fact]
    public async Task Changes_events_are_read_and_comments_skipped()
    {
        var id = Guid.CreateVersion7();
        const string Stream = ": connected\n\n: ping\n\nevent: changes\ndata: [{\"type\":\"pulaski.job.started.v1\",\"kind\":\"job\",\"id\":\"{ID}\",\"siteId\":null}]\n\n";
        var batches = new List<IReadOnlyList<LiveChangeDto>>();
        await foreach (var batch in LiveEvents.ReadAsync(new StringReader(Stream.Replace("{ID}", id.ToString(), StringComparison.Ordinal)), CancellationToken.None))
        {
            batches.Add(batch);
        }

        var change = Assert.Single(Assert.Single(batches));
        Assert.Equal(("job", id), (change.Kind, change.Id!.Value));
    }

    [Fact]
    public void A_page_cares_about_its_kinds_at_its_site_and_one_thing_by_id()
    {
        Guid site = Guid.CreateVersion7(), other = Guid.CreateVersion7(), job = Guid.CreateVersion7();
        var floor = LiveFilters.At(() => site, "job", LiveFilters.Machine);
        Assert.True(floor(new LiveChangeDto("t", "job", job, site)));
        Assert.True(floor(new LiveChangeDto("machine", "machine", job, null)));
        Assert.False(floor(new LiveChangeDto("t", "job", job, other)));
        Assert.False(floor(new LiveChangeDto("t", "lot", job, site)));
        Assert.True(LiveFilters.To("job", () => job)(new LiveChangeDto("t", "job", job, site)));
        Assert.False(LiveFilters.To("job", () => job)(new LiveChangeDto("t", "job", Guid.CreateVersion7(), site)));
    }

    [Fact]
    public async Task A_burst_refreshes_once_now_and_once_after_never_two_at_once()
    {
        var feed = new LiveFeed(null!, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveFeed>.Instance);
        var running = 0;
        var most = 0;
        var refreshes = 0;
        var gate = new TaskCompletionSource();
        using var subscription = new LiveSubscription(_ => true, async () =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await gate.Task;
            Interlocked.Increment(ref refreshes);
            Interlocked.Decrement(ref running);
        }, TimeProvider.System, feed, TimeSpan.FromMilliseconds(20));

        subscription.Poke();
        subscription.Poke();
        subscription.Poke();
        gate.SetResult();
        for (var i = 0; i < 100 && Volatile.Read(ref refreshes) < 2; i++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(100);
        Assert.Equal((2, 1), (refreshes, most));
    }
}
