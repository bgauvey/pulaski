using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

/// <summary>How a screen that refreshes itself says how fresh it is (D-070, D-099).</summary>
public class LiveRefreshTests
{
    private static readonly DateTimeOffset LoadedAt = new(2026, 11, 14, 14, 3, 0, TimeSpan.Zero);

    [Fact]
    public void It_is_live_for_a_minute_after_a_load_then_stale()
    {
        Assert.Equal("Connecting…", LiveRefresh.Status(null, LoadedAt, null));
        Assert.Equal("Live, updated 14:03", LiveRefresh.Status(LoadedAt, LoadedAt.AddSeconds(60), null));
        Assert.False(LiveRefresh.IsStale(LoadedAt, LoadedAt.AddSeconds(60)));
        Assert.Equal("Stale since 14:03", LiveRefresh.Status(LoadedAt, LoadedAt.AddSeconds(61), null));
        Assert.True(LiveRefresh.IsStale(LoadedAt, LoadedAt.AddSeconds(61)));
        Assert.False(LiveRefresh.IsStale(null, LoadedAt));
    }

    [Fact]
    public void Times_are_the_sites()
    {
        Assert.Equal("Live, updated 09:03", LiveRefresh.Status(LoadedAt, LoadedAt, "America/New_York"));
    }
}
