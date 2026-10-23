using Pulaski.Domain.Shifts;

namespace Pulaski.Domain.MachineData;

/// <summary>
/// What a stretch of a machine's time was used for (D-052). Ordered worst first: a line whose machines run in series takes the
/// worst of theirs, one whose machines run in parallel the best (D-050).
/// </summary>
public enum TimeUse
{
    /// <summary>Not running while it should: availability loss. Includes no signal and stops not yet coded.</summary>
    Down,

    /// <summary>A stop with a <c>planned</c> reason (setup, changeover, cleaning, planned maintenance): left out of planned time.</summary>
    Planned,

    /// <summary>In a unit mode that isn't production time (Maintenance, Manual): left out of planned time.</summary>
    NonProduction,

    /// <summary>Executing, in a micro-stop, or in a stop coded with a <c>runtime</c> reason.</summary>
    Run,
}

/// <summary>A machine's state from a point on, as <c>ts.equipment_state_change</c> records it.</summary>
public sealed record StatePoint(DateTimeOffset At, int PackMlState, int UnitMode);

/// <summary>A stop as <c>ts.downtime</c> holds it, with the class of its effective reason (null: not coded).</summary>
public sealed record StopSpan(DateTimeOffset Start, DateTimeOffset? End, Guid? ReasonCodeId, string? UtilizationClass, bool IsMicroStop);

/// <summary>A stretch of time and its use. <see cref="StartsStop"/> marks the first stretch of a stop, so stops are counted once.</summary>
public sealed record UseSpan(DateTimeOffset Start, DateTimeOffset End, TimeUse Use, Guid? ReasonCodeId, bool IsMicroStop, bool NoSignal, bool StartsStop)
{
    public TimeSpan Length => End - Start;
}

/// <summary>Time the equipment's calendar says it works (D-038): a shift, or a whole day for equipment without shifts.</summary>
public sealed record WorkWindow(DateTimeOffset Start, DateTimeOffset End, DateOnly ProductionDate, string? ShiftCode, string? ShiftName = null, string? Crew = null);

/// <summary>A run on the equipment (D-035); an open run lasts until now.</summary>
public sealed record RunWindow(Guid RunId, DateTimeOffset Start, DateTimeOffset? End);

/// <summary>A stretch of working time: its use, the window (shift) it fell in, and the run open then, if any.</summary>
public sealed record UseSlice(UseSpan Span, int Window, Guid? RunId);

/// <summary>
/// Utilization from machine history (D-050, D-052), worked out on read. A machine's time is first classified, then cut to its
/// working windows and runs; time outside the calendar is left out.
/// </summary>
public static class Utilization
{
    /// <summary>
    /// What each stretch of <c>[from, to)</c> was used for. A unit mode that isn't production time comes first; then a stop's
    /// reason class (a micro-stop is run time); otherwise executing is run time and anything else is down. Before the first state
    /// the machine reported, it had no signal (D-005), so missing data shows as availability loss rather than vanishing.
    /// </summary>
    /// <param name="states">Changes in the period, and the last one before it; any order.</param>
    /// <param name="stops">The stops overlapping the period; they don't overlap one another.</param>
    /// <param name="productionModes">The unit modes that are production time (D-005: Production by default).</param>
    /// <param name="from">The period's start.</param>
    /// <param name="to">Its end, at most now.</param>
    public static IReadOnlyList<UseSpan> Classify(
        IReadOnlyList<StatePoint> states, IReadOnlyList<StopSpan> stops, IReadOnlySet<int> productionModes, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            return [];
        }

        var points = new SortedSet<DateTimeOffset> { from, to };
        foreach (var state in states.Where(s => s.At > from && s.At < to))
        {
            points.Add(state.At);
        }

        foreach (var stop in stops)
        {
            if (stop.Start > from && stop.Start < to)
            {
                points.Add(stop.Start);
            }

            if (stop.End is { } end && end > from && end < to)
            {
                points.Add(end);
            }
        }

        var ordered = states.OrderBy(s => s.At).ToList();
        var orderedStops = stops.Where(s => (s.End ?? to) > s.Start).OrderBy(s => s.Start).ToList();
        var bounds = points.ToList();
        var result = new List<UseSpan>();
        StatePoint? current = null;
        int si = 0, pi = 0;
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var (start, end) = (bounds[i], bounds[i + 1]);
            while (si < ordered.Count && ordered[si].At <= start)
            {
                current = ordered[si++];
            }

            while (pi < orderedStops.Count && (orderedStops[pi].End ?? to) <= start)
            {
                pi++;
            }

            var stop = pi < orderedStops.Count && orderedStops[pi].Start <= start ? orderedStops[pi] : null;
            var mode = current?.UnitMode ?? LiveStateRules.DefaultUnitMode;
            var noSignal = (current?.PackMlState ?? PackMlStates.Undefined) == PackMlStates.Undefined;
            UseSpan span;
            if (!productionModes.Contains(mode))
            {
                span = new(start, end, TimeUse.NonProduction, null, false, false, false);
            }
            else if (stop is not null)
            {
                var starts = stop.Start == start;
                span = stop.IsMicroStop
                    ? new(start, end, TimeUse.Run, null, true, false, starts)
                    : stop.UtilizationClass switch
                    {
                        UtilizationClasses.Planned => new(start, end, TimeUse.Planned, stop.ReasonCodeId, false, false, starts),
                        UtilizationClasses.Runtime => new(start, end, TimeUse.Run, stop.ReasonCodeId, false, false, starts),
                        _ => new(start, end, TimeUse.Down, stop.ReasonCodeId, false, noSignal, starts),
                    };
            }
            else if (current?.PackMlState == LiveStateRules.Execute)
            {
                span = new(start, end, TimeUse.Run, null, false, false, false);
            }
            else
            {
                span = new(start, end, TimeUse.Down, null, false, noSignal, false);
            }

            Append(result, span);
        }

        return result;
    }

    /// <summary>
    /// A line's time from its machines' (D-050): in series the worst use at each moment, in parallel the best; ties go to the
    /// first machine. A stop of the line starts whenever it goes down or into a planned stop from anything else. With no
    /// machines it had no signal.
    /// </summary>
    public static IReadOnlyList<UseSpan> Combine(string derivation, IReadOnlyList<IReadOnlyList<UseSpan>> machines, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            return [];
        }

        if (machines.Count == 0)
        {
            return [new UseSpan(from, to, TimeUse.Down, null, false, true, false)];
        }

        var points = new SortedSet<DateTimeOffset> { from, to };
        foreach (var span in machines.SelectMany(m => m))
        {
            if (span.Start > from && span.Start < to)
            {
                points.Add(span.Start);
            }
        }

        var bounds = points.ToList();
        var cursors = new int[machines.Count];
        var result = new List<UseSpan>();
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var (start, end) = (bounds[i], bounds[i + 1]);
            UseSpan? chosen = null;
            for (var m = 0; m < machines.Count; m++)
            {
                var spans = machines[m];
                while (cursors[m] < spans.Count && spans[cursors[m]].End <= start)
                {
                    cursors[m]++;
                }

                var at = cursors[m] < spans.Count && spans[cursors[m]].Start <= start
                    ? spans[cursors[m]]
                    : new UseSpan(start, end, TimeUse.Down, null, false, true, false);
                if (chosen is null || (derivation == StateDerivations.Parallel ? at.Use > chosen.Use : at.Use < chosen.Use))
                {
                    chosen = at;
                }
            }

            var previous = result.Count > 0 ? result[^1].Use : (TimeUse?)null;
            var stopped = chosen!.Use is TimeUse.Down or TimeUse.Planned;
            var startsStop = stopped ? previous is not null and not (TimeUse.Down or TimeUse.Planned) : chosen.IsMicroStop && chosen.StartsStop && chosen.Start == start;
            Append(result, new UseSpan(start, end, chosen.Use, chosen.ReasonCodeId, chosen.IsMicroStop, chosen.NoSignal, startsStop));
        }

        return result;
    }

    /// <summary>
    /// The working windows of production days <paramref name="from"/> to <paramref name="to"/>, up to <paramref name="until"/> (now):
    /// the schedule's shifts that count towards those days, or with no schedule (around the clock) each whole site-local day.
    /// </summary>
    public static IReadOnlyList<WorkWindow> Windows(ShiftSchedule? schedule, TimeZoneInfo zone, DateOnly from, DateOnly to, DateTimeOffset until)
    {
        IEnumerable<WorkWindow> windows = schedule is null
            ? Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
                .Select(d => from.AddDays(d))
                .Select(d => new WorkWindow(DayStart(d, zone), DayStart(d.AddDays(1), zone), d, null))
            : schedule.Expand(zone, DayStart(from, zone), DayStart(to.AddDays(2), zone))
                .Where(o => o.ProductionDate >= from && o.ProductionDate <= to)
                .Select(o => new WorkWindow(o.Start, o.End, o.ProductionDate, o.Code, o.Name, o.Crew));
        return [.. windows.Where(w => w.Start < until).Select(w => w.End > until ? w with { End = until } : w).OrderBy(w => w.Start)];
    }

    /// <summary>Midnight starting a site-local day.</summary>
    public static DateTimeOffset DayStart(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The spans cut to the working windows, and within them to the runs; time outside every window is left out.</summary>
    public static IReadOnlyList<UseSlice> Slice(IReadOnlyList<UseSpan> spans, IReadOnlyList<WorkWindow> windows, IReadOnlyList<RunWindow> runs, DateTimeOffset now)
    {
        var orderedRuns = runs.OrderBy(r => r.Start).ToList();
        var result = new List<UseSlice>();
        for (var w = 0; w < windows.Count; w++)
        {
            var window = windows[w];
            foreach (var span in spans)
            {
                var start = Max(span.Start, window.Start);
                var end = Min(span.End, window.End);
                if (start >= end)
                {
                    continue;
                }

                // Cut by the runs open in it; the gaps between them are time without a job.
                var at = start;
                foreach (var run in orderedRuns)
                {
                    var runStart = Max(run.Start, at);
                    var runEnd = Min(run.End ?? now, end);
                    if (runStart >= runEnd)
                    {
                        continue;
                    }

                    if (runStart > at)
                    {
                        result.Add(Piece(span, at, runStart, w, null));
                    }

                    result.Add(Piece(span, runStart, runEnd, w, run.RunId));
                    at = runEnd;
                }

                if (at < end)
                {
                    result.Add(Piece(span, at, end, w, null));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// A slice inside the run of a job that counts as planned downtime (planned maintenance, say): planned time, whatever the
    /// machine did, unless its unit mode already left it out.
    /// </summary>
    public static UseSlice InPlannedJob(UseSlice slice) =>
        slice.Span.Use == TimeUse.NonProduction
            ? slice
            : slice with { Span = slice.Span with { Use = TimeUse.Planned, ReasonCodeId = null, IsMicroStop = false, NoSignal = false, StartsStop = false } };

    /// <summary>The window a count made at <paramref name="at"/> belongs to: the one it falls in, else the last that started before it; -1 for none.</summary>
    public static int WindowOf(IReadOnlyList<WorkWindow> windows, DateTimeOffset at)
    {
        var last = -1;
        for (var w = 0; w < windows.Count && windows[w].Start <= at; w++)
        {
            if (at < windows[w].End)
            {
                return w;
            }

            last = w;
        }

        return last;
    }

    private static UseSlice Piece(UseSpan span, DateTimeOffset start, DateTimeOffset end, int window, Guid? runId) =>
        new(span with { Start = start, End = end, StartsStop = span.StartsStop && start == span.Start }, window, runId);

    // Joins a span onto the one before it when nothing about them differs, so a run of identical stretches is one.
    private static void Append(List<UseSpan> spans, UseSpan span)
    {
        if (spans.Count > 0 && spans[^1] is var last && last.End == span.Start && !span.StartsStop
            && (last.Use, last.ReasonCodeId, last.IsMicroStop, last.NoSignal) == (span.Use, span.ReasonCodeId, span.IsMicroStop, span.NoSignal))
        {
            spans[^1] = last with { End = span.End };
            return;
        }

        spans.Add(span);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}

/// <summary>
/// Availability, performance and quality over some working time (D-052). Performance and quality weigh output by the ideal
/// rate (earned hours = quantity ÷ ideal rate), so products made in different units add up; output of a run without an ideal
/// rate counts in its quantities but not in either.
/// </summary>
public sealed class OeeTally
{
    public TimeSpan Scheduled { get; private set; }
    public TimeSpan NonProduction { get; private set; }
    public TimeSpan PlannedStop { get; private set; }
    public TimeSpan Run { get; private set; }
    public TimeSpan Down { get; private set; }
    public TimeSpan Uncoded { get; private set; }
    public TimeSpan NoSignal { get; private set; }
    public TimeSpan MicroStop { get; private set; }

    /// <summary>Run time with no job running: output then isn't reported, so it isn't in performance.</summary>
    public TimeSpan RunWithoutJob { get; private set; }

    /// <summary>Run time in runs with an ideal rate: the time performance is measured against.</summary>
    public TimeSpan RunWithRate { get; private set; }

    public int Stops { get; private set; }
    public int MicroStops { get; private set; }
    public decimal Good { get; private set; }
    public decimal Scrap { get; private set; }
    public decimal EarnedHours { get; private set; }
    public decimal EarnedGoodHours { get; private set; }

    /// <summary>Scheduled time less non-production modes and planned stops.</summary>
    public TimeSpan Planned => Scheduled - NonProduction - PlannedStop;

    public decimal? Availability => Planned > TimeSpan.Zero ? Hours(Run) / Hours(Planned) : null;

    public decimal? Performance => RunWithRate > TimeSpan.Zero ? EarnedHours / Hours(RunWithRate) : null;

    public decimal? Quality => EarnedHours > 0 ? EarnedGoodHours / EarnedHours : null;

    public decimal? Oee => Availability is { } a && Performance is { } p && Quality is { } q ? a * p * q : null;

    public void Add(UseSlice slice, bool runHasRate)
    {
        var span = slice.Span;
        var length = span.Length;
        Scheduled += length;
        switch (span.Use)
        {
            case TimeUse.NonProduction:
                NonProduction += length;
                break;
            case TimeUse.Planned:
                PlannedStop += length;
                break;
            case TimeUse.Down:
                Down += length;
                Uncoded += span.ReasonCodeId is null ? length : TimeSpan.Zero;
                NoSignal += span.NoSignal ? length : TimeSpan.Zero;
                break;
            case TimeUse.Run:
                Run += length;
                MicroStop += span.IsMicroStop ? length : TimeSpan.Zero;
                RunWithoutJob += slice.RunId is null ? length : TimeSpan.Zero;
                RunWithRate += slice.RunId is not null && runHasRate ? length : TimeSpan.Zero;
                break;
        }

        if (span.StartsStop)
        {
            if (span.IsMicroStop)
            {
                MicroStops++;
            }
            else
            {
                Stops++;
            }
        }
    }

    /// <param name="processed">Good and scrap together, as reported (D-035).</param>
    /// <param name="defective">Scrap.</param>
    /// <param name="idealRatePerHour">The run's ideal rate in the unit of the quantities; null when it has none.</param>
    public void AddCount(decimal processed, decimal defective, decimal? idealRatePerHour)
    {
        Good += processed - defective;
        Scrap += defective;
        if (idealRatePerHour is > 0 and var rate)
        {
            EarnedHours += processed / rate;
            EarnedGoodHours += (processed - defective) / rate;
        }
    }

    public static decimal Hours(TimeSpan span) => (decimal)span.Ticks / TimeSpan.TicksPerHour;
}
