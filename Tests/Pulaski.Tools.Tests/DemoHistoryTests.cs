using Pulaski.DemoHistory;

namespace Pulaski.Tools.Tests;

/// <summary>Which shifts the demo history fills, and how line 2's machine counts while it runs (D-101).</summary>
public class DemoHistoryTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Theory]
    // 09:30 CST is in the day shift (06:00): the previous one is the night shift from 22:00.
    [InlineData("2026-11-24T15:30:00Z", "2026-11-24T04:00:00Z", "2026-11-24T12:00:00Z")]
    // 15:07 CST, the evening shift (14:00); the day shift before it.
    [InlineData("2026-11-24T21:07:00Z", "2026-11-24T12:00:00Z", "2026-11-24T20:00:00Z")]
    // 01:00 CST, the night shift that started the evening before.
    [InlineData("2026-11-25T07:00:00Z", "2026-11-24T20:00:00Z", "2026-11-25T04:00:00Z")]
    public void The_history_fills_the_previous_shift_and_the_current_one(string now, string previous, string current)
    {
        var window = HistoryWindow.For(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), Chicago);

        Assert.Equal(DateTimeOffset.Parse(previous, System.Globalization.CultureInfo.InvariantCulture), window.PreviousStart);
        Assert.Equal(DateTimeOffset.Parse(current, System.Globalization.CultureInfo.InvariantCulture), window.CurrentStart);
    }

    [Fact]
    public void A_shift_spanning_a_change_of_clocks_keeps_its_site_times()
    {
        // 1 November 2026, 02:00 CDT becomes 01:00 CST: the night shift from 22:00 CDT runs nine hours to 06:00 CST.
        var window = HistoryWindow.For(new DateTimeOffset(2026, 11, 1, 13, 0, 0, TimeSpan.Zero), Chicago);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero), window.PreviousStart);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), window.CurrentStart);
        Assert.Equal(TimeSpan.FromHours(9), window.PreviousLength);
    }

    [Fact]
    public void Moments_of_the_previous_shift_stay_in_it()
    {
        var window = new HistoryWindow(new DateTimeOffset(2026, 11, 24, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 24, 15, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 11, 24, 7, 20, 0, TimeSpan.Zero), window.Previous(3, 20));
        Assert.True(window.Previous(9) < window.CurrentStart);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 13, 30, 0, TimeSpan.Zero), window.Current(1, 30));
    }

    [Fact]
    public void The_line_counts_only_while_it_executes()
    {
        var start = new DateTimeOffset(2026, 11, 24, 4, 0, 0, TimeSpan.Zero);
        var feed = new MachineFeed(start, start.AddHours(2), 10m, seed: 1)
            .State(start.AddMinutes(10), MachineFeed.Execute)
            .Stop(start.AddMinutes(40), TimeSpan.FromMinutes(20), MachineFeed.Stopped);

        Assert.Equal(0, feed.Counted(start, start.AddMinutes(10)));
        Assert.Equal(0, feed.Counted(start.AddMinutes(41), start.AddMinutes(60)));
        var running = feed.Counted(start.AddMinutes(10), start.AddMinutes(40));
        Assert.InRange(running, 30 * 9, 30 * 11);

        var signals = feed.Signals().ToList();
        Assert.Equal([MachineFeed.Idle, MachineFeed.Execute, MachineFeed.Stopped, MachineFeed.Execute],
            signals.Where(s => s.Role == "state").Select(s => (int)s.Value));
        Assert.True(signals.Where(s => s.Role == "processed_count").Select(s => s.Value).Zip(signals.Where(s => s.Role == "processed_count").Skip(1).Select(s => s.Value))
            .All(p => p.Second > p.First));
    }
}
