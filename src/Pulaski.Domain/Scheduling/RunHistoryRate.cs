using System.Globalization;

namespace Pulaski.Domain.Scheduling;

/// <summary>
/// One ended run of a production job of the material on the line, with its good output in the run standard's unit
/// (null when the job's unit doesn't convert to it).
/// </summary>
public sealed record RunSample(Guid RunId, string JobNumber, int RunNo, DateTimeOffset StartedAt, DateTimeOffset EndedAt, decimal? GoodQty)
{
    public decimal Hours => (decimal)(EndedAt - StartedAt).TotalHours;
}

/// <summary>A run as the calculation saw it: its rate, and why it was left out, if it was.</summary>
public sealed record RunSampleVerdict(RunSample Run, decimal? RatePerHour, string? Excluded)
{
    public bool IsUsed => Excluded is null;
}

/// <summary>
/// A planning rate worked out from run history (D-045), with everything needed to explain it. <see cref="RatePerHour"/> is null,
/// with <see cref="NotEnough"/> saying why, when the history is too thin to plan with.
/// </summary>
public sealed record RunHistoryResult(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<RunSampleVerdict> Runs,
    decimal UsedHours,
    decimal UsedGoodQty,
    decimal? SlowFence,
    decimal? FastFence,
    decimal? RatePerHour,
    string? NotEnough)
{
    public int UsedRuns => Runs.Count(r => r.IsUsed);
}

/// <summary>
/// Works out a planning rate from what a line actually made (D-045): good output over run time, across the ended runs of the
/// material on the line in a window. Safeguards: short runs, runs with no good output and runs in a unit that doesn't
/// convert are left out; with enough runs, unusually slow or fast ones are left out by Tukey's fences (1.5 × the
/// interquartile range), never closer than 10% either side of the median so a line that runs very steadily keeps its normal
/// spread; and with too few runs or hours left there is no rate. Pure and repeatable, so a reviewed rate
/// can be checked again when it is accepted.
/// </summary>
public static class RunHistoryRate
{
    public const int DefaultDays = 90;
    public const int MinDays = 7;
    public const int MaxDays = 365;
    public const int MinRuns = 5;
    public const decimal MinHours = 8m;

    /// <summary>The fences need this many runs to mean anything.</summary>
    public const int MinRunsForFences = 4;

    public static readonly TimeSpan MinRunLength = TimeSpan.FromMinutes(30);

    /// <summary>The fences are at least this share of the median away from it.</summary>
    public const decimal MinFenceSpread = 0.10m;

    public static RunHistoryResult Calculate(IReadOnlyList<RunSample> runs, DateTimeOffset from, DateTimeOffset to)
    {
        var verdicts = runs.OrderBy(r => r.StartedAt).Select(Judge).ToList();

        var rates = verdicts.Where(v => v.IsUsed).Select(v => v.RatePerHour!.Value).Order().ToList();
        decimal? slow = null, fast = null;
        if (rates.Count >= MinRunsForFences)
        {
            var (q1, median, q3) = (Quantile(rates, 0.25m), Quantile(rates, 0.5m), Quantile(rates, 0.75m));
            slow = Math.Min(q1 - 1.5m * (q3 - q1), median * (1 - MinFenceSpread));
            fast = Math.Max(q3 + 1.5m * (q3 - q1), median * (1 + MinFenceSpread));
            verdicts = [.. verdicts.Select(v => v switch
            {
                { IsUsed: true, RatePerHour: var r } when r < slow => v with { Excluded = $"Unusually slow: under {Rate(slow.Value)} an hour" },
                { IsUsed: true, RatePerHour: var r } when r > fast => v with { Excluded = $"Unusually fast: over {Rate(fast.Value)} an hour" },
                _ => v,
            })];
        }

        var used = verdicts.Where(v => v.IsUsed).ToList();
        var hours = used.Sum(v => v.Run.Hours);
        var good = used.Sum(v => v.Run.GoodQty!.Value);
        var notEnough = used.Count < MinRuns
            ? $"Only {used.Count} usable {(used.Count == 1 ? "run" : "runs")}; a rate needs at least {MinRuns}."
            : hours < MinHours
                ? $"Only {Rate(hours)} hours of usable runs; a rate needs at least {MinHours.ToString(CultureInfo.InvariantCulture)}."
                : null;

        return new RunHistoryResult(
            from, to, verdicts, Math.Round(hours, 2), good, slow, fast,
            notEnough is null ? Math.Round(good / hours, 2) : null, notEnough);
    }

    private static RunSampleVerdict Judge(RunSample run)
    {
        if (run.GoodQty is not { } good)
        {
            return new(run, null, "The job's unit doesn't convert to the planning rate's unit");
        }

        if (run.EndedAt - run.StartedAt < MinRunLength)
        {
            return new(run, null, "Shorter than 30 minutes");
        }

        return good <= 0 ? new(run, null, "No good output") : new(run, Math.Round(good / run.Hours, 4), null);
    }

    // Linear interpolation between the closest ranks, on sorted values.
    private static decimal Quantile(List<decimal> sorted, decimal p)
    {
        var position = (sorted.Count - 1) * p;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static string Rate(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);
}
