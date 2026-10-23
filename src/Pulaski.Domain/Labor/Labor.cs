namespace Pulaski.Domain.Labor;

/// <summary>What a person's time was spent on (D-040).</summary>
public static class LaborTypes
{
    /// <summary>Running a line: shared out to the runs on it while they ran.</summary>
    public const string Direct = "direct";

    /// <summary>Getting a job ready: changeover, setting up. Counts towards the job it names.</summary>
    public const string Setup = "setup";

    /// <summary>Not on production (cleaning, meetings, waiting): needs a reason.</summary>
    public const string Indirect = "indirect";

    public const string Training = "training";

    public static IReadOnlyList<string> All { get; } = [Direct, Setup, Indirect, Training];
}

/// <summary>How a session was recorded (D-040).</summary>
public static class LaborSources
{
    /// <summary>The person clocked themselves on.</summary>
    public const string Self = "self";

    /// <summary>A supervisor recorded it for them.</summary>
    public const string Supervisor = "supervisor";

    /// <summary>A supervisor checked the crew onto a line.</summary>
    public const string Crew = "crew";

    /// <summary>Replaces a session of an approved shift.</summary>
    public const string Correction = "correction";

    public static IReadOnlyList<string> All { get; } = [Self, Supervisor, Crew, Correction];
}

/// <summary>A stretch of time a person spent (D-040), as the rules see it.</summary>
public sealed record LaborSpan(
    Guid Id, Guid PersonId, Guid EquipmentId, Guid? JobId, string LaborType, DateTimeOffset Start, DateTimeOffset? End, decimal AllocationPct);

/// <summary>
/// A break a person took (D-047): its time comes out of every session of theirs it overlaps, whatever their share, and is
/// reported on its own, paid or unpaid. An open break has no end yet.
/// </summary>
public sealed record BreakSpan(Guid PersonId, DateTimeOffset Start, DateTimeOffset? End, bool IsPaid);

/// <summary>A run's time on its line, for sharing labor out to it.</summary>
public sealed record RunSpan(Guid RunId, Guid JobId, Guid EquipmentId, DateTimeOffset Start, DateTimeOffset? End);

/// <summary>Hours of one session that count towards a run (or, with no run, towards nothing running).</summary>
public sealed record LaborShare(Guid SessionId, Guid? RunId, Guid? JobId, string LaborType, decimal Hours);

public static class LaborRules
{
    /// <summary>How long before a shift someone may clock on and count towards it.</summary>
    public static readonly TimeSpan ShiftLead = TimeSpan.FromHours(2);

    /// <summary>How far ahead of the clock a time may be entered, for clocks that differ a little.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <returns>What is wrong with a session, in words; null when it can be saved.</returns>
    public static string? Problem(string laborType, Guid? reasonCodeId, Guid? jobId, DateTimeOffset start, DateTimeOffset? end, decimal allocationPct, DateTimeOffset now)
    {
        if (!LaborTypes.All.Contains(laborType))
        {
            return "Direct, setup, indirect or training.";
        }

        if (laborType == LaborTypes.Indirect && reasonCodeId is null)
        {
            return "Indirect time needs a reason.";
        }

        if (laborType == LaborTypes.Setup && jobId is null)
        {
            return "Setup time is for a job; choose it.";
        }

        if (allocationPct is <= 0 or > 100)
        {
            return "The share of their time is more than 0% and at most 100%.";
        }

        if (start > now + ClockSkew || end > now + ClockSkew)
        {
            return "Time can't be recorded ahead of now.";
        }

        return end < start ? "It ends before it starts." : null;
    }

    /// <summary>
    /// Whether a person may take on another share of their time: open sessions together are at most 100% (someone
    /// tending two machines is 50% on each).
    /// </summary>
    public static string? AllocationProblem(IEnumerable<decimal> openShares, decimal newShare) =>
        openShares.Sum() + newShare > 100
            ? $"They are already on {openShares.Sum():0.##}% of their time; another {newShare:0.##}% would be more than all of it. End a session or lower its share."
            : null;

    /// <summary>The longest one break may last, so a forgotten one can't swallow a shift.</summary>
    public static readonly TimeSpan MaxBreak = TimeSpan.FromHours(4);

    /// <returns>What is wrong with a break's times, in words; null when it can be saved.</returns>
    public static string? BreakProblem(DateTimeOffset start, DateTimeOffset? end, DateTimeOffset now)
    {
        if (start > now + ClockSkew || end > now + ClockSkew)
        {
            return "A break can't be recorded ahead of now.";
        }

        if (end is { } e && e <= start)
        {
            return "It ends after it starts.";
        }

        return end is { } ended && ended - start > MaxBreak ? "A break is at most 4 hours; end the person's time instead." : null;
    }

    /// <summary>
    /// The parts of <c>[start, end)</c> outside the breaks (D-047), in order. A session's worked time is these parts; its
    /// own start, end and share never change.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> WorkingPieces(
        DateTimeOffset start, DateTimeOffset end, IEnumerable<BreakSpan> breaks, DateTimeOffset now)
    {
        var pieces = new List<(DateTimeOffset, DateTimeOffset)>();
        var cursor = start;
        foreach (var (from, to) in breaks.Select(b => (b.Start, b.End ?? now)).Where(b => b.Item2 > start && b.Start < end).OrderBy(b => b.Start))
        {
            if (from > cursor)
            {
                pieces.Add((cursor, from));
            }

            cursor = Max(cursor, to);
        }

        if (end > cursor)
        {
            pieces.Add((cursor, end));
        }

        return pieces;
    }

    /// <summary>A session's worked hours (D-047): elapsed time outside the person's breaks × its share.</summary>
    public static decimal WorkedHours(DateTimeOffset start, DateTimeOffset? end, decimal allocationPct, IEnumerable<BreakSpan> breaks, DateTimeOffset now) =>
        WorkingPieces(start, end ?? now, breaks, now).Sum(p => Hours(p.Start, p.End)) * allocationPct / 100m;

    /// <summary>
    /// A person's break hours (D-047), paid and unpaid: each break counts only while they were on (inside one of their
    /// sessions), and once however many sessions it overlaps.
    /// </summary>
    public static (decimal Paid, decimal Unpaid) BreakHours(IEnumerable<BreakSpan> breaks, IEnumerable<LaborSpan> sessions, DateTimeOffset now)
    {
        var on = Union(sessions.Select(s => (s.Start, s.End ?? now)));
        decimal paid = 0, unpaid = 0;
        foreach (var b in breaks)
        {
            var hours = on.Sum(o => Hours(Max(o.Start, b.Start), Min(o.End, b.End ?? now)));
            if (b.IsPaid)
            {
                paid += hours;
            }
            else
            {
                unpaid += hours;
            }
        }

        return (Round(paid), Round(unpaid));
    }

    /// <summary>
    /// Shares each session's time out (D-040). Direct time with no job named counts towards whatever ran on its line
    /// while it lasted, by overlap; time naming a job counts towards that job's runs on the line; setup counts towards
    /// its job even with nothing running. Hours are elapsed time × the session's share; time with nothing running is
    /// kept, with no run, so idle labor still shows. Indirect and training count towards no job. The person's breaks
    /// (D-047) come out of every session first.
    /// </summary>
    public static IReadOnlyList<LaborShare> Attribute(
        IEnumerable<LaborSpan> sessions, IReadOnlyList<RunSpan> runs, DateTimeOffset now, IReadOnlyList<BreakSpan>? breaks = null)
    {
        var shares = new List<LaborShare>();
        foreach (var s in sessions)
        {
            var end = s.End ?? now;
            var share = s.AllocationPct / 100m;
            var pieces = WorkingPieces(s.Start, end, breaks?.Where(b => b.PersonId == s.PersonId) ?? [], now);
            var total = pieces.Sum(p => Hours(p.Start, p.End)) * share;
            if (s.LaborType is LaborTypes.Indirect or LaborTypes.Training)
            {
                shares.Add(new LaborShare(s.Id, null, null, s.LaborType, Round(total)));
                continue;
            }

            var counted = 0m;
            foreach (var run in runs.Where(r => r.EquipmentId == s.EquipmentId && (s.JobId is null || r.JobId == s.JobId)))
            {
                var hours = pieces.Sum(p => Hours(Max(p.Start, run.Start), Min(p.End, run.End ?? now))) * share;
                if (hours <= 0)
                {
                    continue;
                }

                counted += hours;
                shares.Add(new LaborShare(s.Id, run.RunId, run.JobId, s.LaborType, Round(hours)));
            }

            // Setup is for its job even when nothing runs; other time with nothing running stays unattributed.
            if (total - counted > 0.0001m)
            {
                shares.Add(new LaborShare(s.Id, null, s.LaborType == LaborTypes.Setup ? s.JobId : null, s.LaborType, Round(total - counted)));
            }
        }

        return shares;
    }

    /// <summary>
    /// Labor efficiency of a run (D-040): the standard labor (run hours × the line's standard crew size for the material)
    /// against the direct hours shared out to it, as a percentage. Null without a standard or without labor.
    /// </summary>
    public static decimal? Efficiency(decimal runHours, decimal? standardCrewSize, decimal directHours) =>
        standardCrewSize is > 0 && directHours > 0 ? Math.Round(runHours * standardCrewSize.Value / directHours * 100m, 1) : null;

    public static decimal Hours(DateTimeOffset from, DateTimeOffset to) => to <= from ? 0 : (decimal)(to - from).TotalHours;

    private static decimal Round(decimal hours) => Math.Round(hours, 4);

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Union(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> spans)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var span in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Max(merged[^1].End, span.End));
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
