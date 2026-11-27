namespace Pulaski.DemoHistory;

/// <summary>
/// Extrusion line 2's machine signals for the history, as its edge would have forwarded them: its PackML state on each change (raw values of
/// the simulators' profile, PACKML-SIM), its cumulative length counter every minute while it executes, and its alarm code. Worked out
/// before the history runs, so production reports can count what the machine counted.
/// </summary>
public sealed class MachineFeed(DateTimeOffset from, DateTimeOffset to, decimal metersPerMinute, int seed)
{
    // The simulators' raw states (deploy/simulator/packml-simulator-profile.csv).
    public const int Idle = 1;
    public const int Stopped = 2;
    public const int Starting = 3;
    public const int Suspended = 5;
    public const int Execute = 6;
    public const int Aborted = 9;
    public const int Resetting = 15;

    public const long CounterStart = 1_250_000;

    private readonly SortedDictionary<DateTimeOffset, int> _states = new() { [from] = Idle };
    private readonly SortedDictionary<DateTimeOffset, int> _alarms = new();
    private List<(DateTimeOffset At, long Raw)>? _counts;

    /// <summary>The line goes into a state at a moment (and stays there until the next).</summary>
    public MachineFeed State(DateTimeOffset at, int state)
    {
        _states[at] = state;
        _counts = null;
        return this;
    }

    /// <summary>The line leaves Execute for a while, then runs again.</summary>
    public MachineFeed Stop(DateTimeOffset at, TimeSpan length, int state) => State(at, state).State(at + length, Execute);

    public MachineFeed Alarm(DateTimeOffset raised, DateTimeOffset cleared, int code)
    {
        _alarms[raised] = code;
        _alarms[cleared] = 0;
        return this;
    }

    public int StateAt(DateTimeOffset at) => _states.LastOrDefault(s => s.Key <= at).Value;

    /// <summary>Meters the counter moved in (after, until].</summary>
    public decimal Counted(DateTimeOffset after, DateTimeOffset until)
    {
        var counts = Counts();
        long At(DateTimeOffset t) => counts.LastOrDefault(c => c.At <= t, (At: from, Raw: CounterStart)).Raw;
        return At(until) - At(after);
    }

    public IReadOnlyList<(DateTimeOffset At, long Raw)> Counts()
    {
        if (_counts is not null)
        {
            return _counts;
        }

        var random = new Random(seed);
        var counts = new List<(DateTimeOffset At, long Raw)> { (from, CounterStart) };
        var raw = CounterStart;
        var carry = 0m;
        for (var minute = from.AddMinutes(1); minute <= to; minute = minute.AddMinutes(1))
        {
            // A minute's length: running only while executing the whole minute, a little under or over the usual speed.
            var running = _states.Where(s => s.Key > minute.AddMinutes(-1) && s.Key <= minute).All(s => s.Value == Execute)
                && StateAt(minute.AddMinutes(-1)) == Execute;
            if (!running)
            {
                continue;
            }

            carry += metersPerMinute * (0.94m + (decimal)random.NextDouble() * 0.1m);
            var whole = (long)carry;
            carry -= whole;
            raw += whole;
            counts.Add((minute, raw));
        }

        _counts = counts;
        return counts;
    }

    /// <summary>Every signal, by tag role, in time order.</summary>
    public IEnumerable<(DateTimeOffset At, string Role, decimal Value)> Signals()
    {
        var all = new List<(DateTimeOffset, string, decimal)> { (from, "unit_mode", 1) };
        all.AddRange(_states.Select(s => (s.Key, "state", (decimal)s.Value)));
        all.AddRange(_alarms.Select(a => (a.Key, "alarm", (decimal)a.Value)));
        all.AddRange(Counts().Select(c => (c.At, "processed_count", (decimal)c.Raw)));
        all.Add((from, "alarm", 0));
        return all.Where(s => s.Item1 <= to).OrderBy(s => s.Item1).ThenBy(s => s.Item2 == "state" ? 0 : 1);
    }
}
