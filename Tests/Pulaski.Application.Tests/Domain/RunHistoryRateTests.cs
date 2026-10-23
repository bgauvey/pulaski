using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Planning rates from run history (D-045): good over run time, with its safeguards.</summary>
public class RunHistoryRateTests
{
    private static readonly DateTimeOffset To = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = To.AddDays(-90);

    private static RunSample Run(int day, double hours, decimal? good) =>
        new(Guid.CreateVersion7(), $"J-{day}", 1, To.AddDays(-day), To.AddDays(-day).AddHours(hours), good);

    [Fact]
    public void The_rate_is_good_output_over_run_time_weighted_by_hours()
    {
        // A 4-hour run at 100/h counts four times as much as a 1-hour run at 108/h: 1,008 in 9 h, not the average of the rates.
        var result = RunHistoryRate.Calculate(
            [Run(1, 4, 400), Run(2, 1, 108), Run(3, 1, 100), Run(4, 1, 100), Run(5, 1, 100), Run(6, 1, 100), Run(7, 1, 100)], From, To);

        Assert.Equal((7, 10m), (result.UsedRuns, result.UsedHours));
        Assert.Equal(Math.Round(1008m / 10m, 2), result.RatePerHour);
        Assert.Null(result.NotEnough);
    }

    [Fact]
    public void Short_empty_and_unconvertible_runs_are_left_out_with_why()
    {
        var result = RunHistoryRate.Calculate([Run(1, 0.25, 50), Run(2, 3, 0), Run(3, 3, null), Run(4, 3, 300)], From, To);

        // Oldest first.
        Assert.Equal(
            [null, "The job's unit doesn't convert to the planning rate's unit", "No good output", "Shorter than 30 minutes"],
            result.Runs.Select(r => r.Excluded));
        Assert.Null(result.RatePerHour);
        Assert.Equal("Only 1 usable run; a rate needs at least 5.", result.NotEnough);
    }

    [Fact]
    public void Unusually_slow_or_fast_runs_are_left_out_by_the_fences()
    {
        // Nine runs near 100/h, one at 20/h after a breakdown and one at 400/h (a report put in the wrong run).
        RunSample[] runs = [.. Enumerable.Range(1, 9).Select(d => Run(d, 2, 196 + d)), Run(20, 2, 40), Run(21, 2, 800)];

        var result = RunHistoryRate.Calculate(runs, From, To);

        Assert.Equal(9, result.UsedRuns);
        Assert.StartsWith("Unusually slow", result.Runs.Single(r => r.Run.JobNumber == "J-20").Excluded, StringComparison.Ordinal);
        Assert.StartsWith("Unusually fast", result.Runs.Single(r => r.Run.JobNumber == "J-21").Excluded, StringComparison.Ordinal);
        Assert.InRange(result.RatePerHour!.Value, 99m, 103m);
        Assert.True(result.SlowFence < 99m && result.FastFence > 103m);
    }

    [Fact]
    public void Enough_runs_but_too_few_hours_give_no_rate()
    {
        var result = RunHistoryRate.Calculate([.. Enumerable.Range(1, 6).Select(d => Run(d, 1, 100))], From, To);

        Assert.Null(result.RatePerHour);
        Assert.Equal("Only 6 hours of usable runs; a rate needs at least 8.", result.NotEnough);
    }
}
