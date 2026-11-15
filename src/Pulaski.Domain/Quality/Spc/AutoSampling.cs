namespace Pulaski.Domain.Quality.Spc;

/// <summary>A process value as recorded: when, the scaled value, and whether it was good (quality 192).</summary>
public sealed record ProcessReading(DateTimeOffset At, double Value, bool IsGood);

/// <summary>
/// Turning a process-value tag's readings into results (D-099): at the end of each interval, aligned to the clock, a subgroup of the value held
/// at consecutive moments a minute apart, ending then. Readings arrive on change and at least once a minute while good, so the value at a moment
/// is the last reading at or before it, if that is good and recent. Pure.
/// </summary>
public static class AutoSampling
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;

    /// <summary>Snapshots in a subgroup are this far apart.</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromMinutes(1);

    /// <summary>A reading older than this at a moment means the signal was lost (the edge sends at least once a minute while good).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    /// <summary>Results are made this long after an interval ends, so the edge's late readings are in.</summary>
    public static readonly TimeSpan Lag = TimeSpan.FromMinutes(2);

    /// <summary>Intervals further back than this are skipped, not caught up.</summary>
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromHours(24);

    /// <exception cref="DomainRuleException">Not 1 to 1440 minutes.</exception>
    public static int EnsureInterval(int minutes) =>
        minutes is >= MinIntervalMinutes and <= MaxIntervalMinutes ? minutes : throw new DomainRuleException("Sample every 1 to 1440 minutes.");

    /// <summary>
    /// The interval ends after <paramref name="after"/> and no later than <paramref name="now"/> less the lag, aligned to whole multiples of the
    /// interval since midnight UTC, at most <paramref name="max"/>; ends older than the catch-up window are skipped.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Ticks(DateTimeOffset after, DateTimeOffset now, int intervalMinutes, int max)
    {
        var step = TimeSpan.FromMinutes(intervalMinutes);
        var until = now - Lag;
        var from = after < now - MaxCatchUp ? now - MaxCatchUp : after;
        var day = new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero);
        var next = day + TimeSpan.FromTicks((from - day).Ticks / step.Ticks * step.Ticks);
        if (next <= from)
        {
            next += step;
        }

        var ticks = new List<DateTimeOffset>();
        for (var t = next; t <= until && ticks.Count < max; t += step)
        {
            ticks.Add(t);
        }

        return ticks;
    }

    /// <summary>The <paramref name="n"/> moments of a subgroup ending at <paramref name="tick"/>, oldest first.</summary>
    public static IReadOnlyList<DateTimeOffset> Moments(DateTimeOffset tick, int n) =>
        [.. Enumerable.Range(0, n).Select(k => tick - Spacing * (n - 1 - k))];

    /// <summary>The subgroup at <paramref name="tick"/>, or null when any moment has no good, recent reading.</summary>
    /// <param name="readings">The tag's readings around the moments, in any order.</param>
    /// <param name="tick">The interval's end.</param>
    /// <param name="n">Values a subgroup holds.</param>
    public static IReadOnlyList<double>? Subgroup(IReadOnlyList<ProcessReading> readings, DateTimeOffset tick, int n)
    {
        var ordered = readings.OrderBy(r => r.At).ToList();
        var values = new List<double>();
        foreach (var moment in Moments(tick, n))
        {
            var held = ordered.LastOrDefault(r => r.At <= moment);
            if (held is null || !held.IsGood || moment - held.At > MaxAge || !double.IsFinite(held.Value))
            {
                return null;
            }

            values.Add(held.Value);
        }

        return values;
    }
}
