using Pulaski.Domain;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Results from a process-value tag (D-099): clock-aligned intervals, consecutive snapshots.</summary>
public class AutoSamplingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Interval_ends_are_aligned_to_the_clock_and_wait_for_late_readings()
    {
        var ticks = AutoSampling.Ticks(Noon.AddMinutes(7), Noon.AddMinutes(47), 15, 10);
        Assert.Equal([Noon.AddMinutes(15), Noon.AddMinutes(30), Noon.AddMinutes(45)], ticks);

        // 12:46 is too soon after 12:45 for its readings to be in.
        Assert.Equal([Noon.AddMinutes(15), Noon.AddMinutes(30)], AutoSampling.Ticks(Noon.AddMinutes(7), Noon.AddMinutes(46), 15, 10));
        Assert.Equal([Noon.AddMinutes(30)], AutoSampling.Ticks(Noon.AddMinutes(15), Noon.AddMinutes(40), 15, 10));
        Assert.Single(AutoSampling.Ticks(Noon, Noon.AddHours(5), 15, 1));
    }

    [Fact]
    public void Old_intervals_are_skipped_not_caught_up()
    {
        // Back 24 hours, and the last hour's end (12:00) waits for its readings: 13:00 yesterday to 11:00 today.
        var ticks = AutoSampling.Ticks(Noon.AddDays(-3), Noon, 60, 100);
        Assert.Equal(23, ticks.Count);
        Assert.Equal(Noon.AddHours(-23), ticks[0]);
        Assert.Equal(Noon.AddHours(-1), ticks[^1]);
        Assert.Throws<DomainRuleException>(() => AutoSampling.EnsureInterval(0));
    }

    [Fact]
    public void A_subgroup_is_the_value_held_at_consecutive_minutes()
    {
        IReadOnlyList<ProcessReading> readings =
        [
            new(Noon.AddMinutes(-3.5), 210.0, true),
            new(Noon.AddMinutes(-1.2), 211.0, true),
            new(Noon.AddSeconds(-10), 212.5, true),
        ];

        Assert.Equal(AutoSampling.Moments(Noon, 3), [Noon.AddMinutes(-2), Noon.AddMinutes(-1), Noon]);
        Assert.Equal([210.0, 211.0, 212.5], AutoSampling.Subgroup(readings, Noon, 3));
        Assert.Equal([212.5], AutoSampling.Subgroup(readings, Noon, 1));
    }

    [Fact]
    public void A_bad_or_stale_reading_skips_the_interval()
    {
        Assert.Null(AutoSampling.Subgroup([new(Noon.AddMinutes(-5), 210, true)], Noon, 1));
        Assert.Null(AutoSampling.Subgroup([new(Noon.AddSeconds(-30), 210, false)], Noon, 1));
        Assert.Null(AutoSampling.Subgroup([new(Noon.AddSeconds(30), 210, true)], Noon, 1));
        Assert.Null(AutoSampling.Subgroup([new(Noon.AddMinutes(-1.5), 210, true), new(Noon.AddSeconds(-5), double.NaN, true)], Noon, 1));
    }
}
