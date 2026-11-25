namespace Pulaski.Domain.Quality;

/// <summary>
/// When a spec item is sampled during a run (#136, D-121): at its start and/or every so many hours or units of output (in the job's unit), with a
/// grace before a due sample counts as overdue. A sample taken up to the grace early counts for the next one.
/// </summary>
public sealed record SamplingPlan(bool AtStart, decimal? EveryHours, decimal? EveryUnits, int GraceMinutes = SamplingPlan.DefaultGrace)
{
    public const int DefaultGrace = 15;
    public const int MaxGrace = 480;
    public const decimal MaxHours = 168;

    public bool IsEmpty => !AtStart && EveryHours is null && EveryUnits is null;

    /// <returns>What is wrong with it, or null.</returns>
    public UserMessage? Problem() =>
        EveryHours is not null && EveryUnits is not null ? UserMessage.Of("Sample every so many hours or every so many units, not both.")
        : EveryHours is <= 0 or > MaxHours ? UserMessage.From($"Sample every so many hours, more than 0 and at most {MaxHours}.")
        : EveryUnits is <= 0 ? UserMessage.Of("Sample every so many units, more than 0.")
        : GraceMinutes is < 0 or > MaxGrace ? UserMessage.From($"Allow 0 to {MaxGrace} minutes' grace.")
        : null;
}

/// <summary>How a run stands for one plan.</summary>
public static class SampleStatus
{
    /// <summary>Nothing is due yet (the next is <see cref="SampleDue.NextAt"/> or after so many more units).</summary>
    public const string Upcoming = "upcoming";

    /// <summary>The latest due sample is taken.</summary>
    public const string Done = "done";

    /// <summary>A sample is due, within its grace.</summary>
    public const string Due = "due";

    /// <summary>A due sample's grace has passed with none taken.</summary>
    public const string Overdue = "overdue";

    public static int Rank(string status) => status switch { Overdue => 0, Due => 1, Upcoming => 2, _ => 3 };
}

/// <summary>
/// A run's standing for one plan: the status, when the latest sample fell due, how many earlier due samples were missed (another fell due before
/// any was taken), and when (or after how many more units) the next falls due.
/// </summary>
public sealed record SampleDue(string Status, DateTimeOffset? DueAt, int Missed, DateTimeOffset? NextAt, decimal? NextAfterUnits, int Taken);

/// <summary>
/// Works out a run's due samples on read (#136): due times are the run's start (if asked), every <c>EveryHours</c> after it, or each moment the
/// run's output first reached a multiple of <c>EveryUnits</c>. Each due time is met by a sample taken from the grace before it until the grace
/// before the next; the latest decides the status. Pure.
/// </summary>
public static class SamplingSchedule
{
    /// <param name="plan">The item's plan.</param>
    /// <param name="start">The run's start.</param>
    /// <param name="end">When it ended, or null while it runs.</param>
    /// <param name="now">Now.</param>
    /// <param name="output">The run's output reports in time order: when, and how much (the job's unit).</param>
    /// <param name="samples">When each result for the characteristic was taken in the run (not excluded).</param>
    public static SampleDue Evaluate(
        SamplingPlan plan, DateTimeOffset start, DateTimeOffset? end, DateTimeOffset now, IReadOnlyList<(DateTimeOffset At, decimal Quantity)> output,
        IReadOnlyList<DateTimeOffset> samples)
    {
        var until = end is { } e && e < now ? e : now;
        var grace = TimeSpan.FromMinutes(plan.GraceMinutes);
        var due = new List<DateTimeOffset>();
        if (plan.AtStart)
        {
            due.Add(start);
        }

        DateTimeOffset? nextAt = null;
        decimal? nextUnits = null;
        if (plan.EveryHours is { } hours)
        {
            var step = TimeSpan.FromHours((double)hours);
            var at = start + step;
            for (; at <= until; at += step)
            {
                due.Add(at);
            }

            nextAt = end is null ? at : null;
        }

        if (plan.EveryUnits is { } units)
        {
            var total = 0m;
            var reached = 0m;
            foreach (var (at, quantity) in output.Where(o => o.At <= until).OrderBy(o => o.At))
            {
                total += quantity;
                while (total >= reached + units)
                {
                    reached += units;
                    due.Add(at);
                }
            }

            nextUnits = end is null ? reached + units - total : null;
        }

        due.Sort();
        var taken = samples.Where(s => s >= start - grace && s <= until).OrderBy(s => s).ToList();
        if (due.Count == 0)
        {
            return new(SampleStatus.Upcoming, null, 0, nextAt, nextUnits, taken.Count);
        }

        bool Met(int i)
        {
            var from = due[i] - grace;
            var to = i + 1 < due.Count ? due[i + 1] - grace : DateTimeOffset.MaxValue;
            return taken.Any(s => s >= from && s < to);
        }

        var missed = Enumerable.Range(0, due.Count - 1).Count(i => !Met(i));
        var latest = due[^1];
        var status = Met(due.Count - 1) ? SampleStatus.Done
            : end is not null ? SampleStatus.Overdue
            : now <= latest + grace ? SampleStatus.Due
            : SampleStatus.Overdue;
        return new(status, latest, missed, nextAt, nextUnits, taken.Count);
    }
}
