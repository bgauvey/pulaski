using System.Collections.Concurrent;
using System.Globalization;
using Pulaski.Connectors;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Edge;

/// <summary>
/// Turns what connectors read into signals for the tags bound to it (D-050), and writes them to the buffer. A tag's value is
/// forwarded when it changes, and when it is lost; a machine speed at most every <see cref="EdgeOptions.SpeedInterval"/>.
/// Addresses nothing is bound to are only kept as seen, for binding.
/// </summary>
public sealed class TagBinder(EdgeBuffer buffer, TimeSpan speedInterval)
{
    private readonly Lock _lock = new();
    private Dictionary<(Guid Connection, string Address), List<EdgeTagConfigDto>> _tags = [];
    private readonly Dictionary<Guid, (decimal? Value, bool Good, DateTimeOffset At)> _last = [];
    private readonly Dictionary<Guid, (decimal Value, DateTimeOffset At)> _pendingSpeed = [];
    private readonly ConcurrentDictionary<(Guid Connection, string Address), EdgeMetricReportDto> _seen = new();
    private readonly Dictionary<(Guid Connection, string Address), ConnectorSample> _current = [];

    /// <summary>Takes on a configuration: tags bound since are read from now on, unbound ones are dropped.</summary>
    /// <summary>
    /// Takes on a configuration: tags bound since are read from now on, starting with the last value seen at their address (a machine
    /// sitting idle doesn't send its state again); unbound ones are dropped.
    /// </summary>
    /// <returns>How many signals the newly bound tags produced.</returns>
    public int Use(EdgeConfigDto config)
    {
        lock (_lock)
        {
            _tags = config.Tags.GroupBy(t => (t.ConnectionId, t.Address)).ToDictionary(g => g.Key, g => g.ToList());
            var bound = config.Tags.Select(t => t.Id).ToHashSet();
            foreach (var gone in _last.Keys.Where(k => !bound.Contains(k)).ToList())
            {
                _last.Remove(gone);
                _pendingSpeed.Remove(gone);
            }

            var written = 0;
            foreach (var tag in config.Tags.Where(t => !_last.ContainsKey(t.Id)))
            {
                if (_current.TryGetValue((tag.ConnectionId, tag.Address), out var sample))
                {
                    written += Write([tag], sample);
                }
            }

            return written;
        }
    }

    /// <summary>The addresses bound on a connection.</summary>
    public IReadOnlySet<string> AddressesOf(Guid connectionId)
    {
        lock (_lock)
        {
            return _tags.Keys.Where(k => k.Connection == connectionId).Select(k => k.Address).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <returns>How many signals the sample produced.</returns>
    public int Accept(Guid connectionId, ConnectorSample sample)
    {
        _seen.AddOrUpdate((connectionId, sample.Address),
            _ => Seen(connectionId, sample.Address, null, sample.Value, sample.Timestamp),
            (_, before) => before with { Value = Text(sample.Value), SeenAt = Max(before.SeenAt, sample.Timestamp) });

        lock (_lock)
        {
            _current[(connectionId, sample.Address)] = sample;
            return _tags.TryGetValue((connectionId, sample.Address), out var tags) ? Write(tags, sample) : 0;
        }
    }

    // Called holding the lock.
    private int Write(List<EdgeTagConfigDto> tags, ConnectorSample sample)
    {
        var value = sample.Quality == SampleQuality.Good ? Number(sample.Value) : null;
        var good = sample.Quality == SampleQuality.Good && value is not null;
        var written = 0;
        foreach (var tag in tags)
        {
            if (_last.TryGetValue(tag.Id, out var last) && last.Good == good && last.Value == value)
            {
                // Back to what was last forwarded: a speed held back meanwhile is no longer news.
                _pendingSpeed.Remove(tag.Id);
                continue;
            }

            if (tag.Role == "machine_speed" && good && _last.TryGetValue(tag.Id, out var previous) && previous.Good
                && sample.Timestamp - previous.At < speedInterval)
            {
                // Too soon: kept, and forwarded by Flush once the interval has passed, unless a newer one comes first.
                _pendingSpeed[tag.Id] = (value!.Value, sample.Timestamp);
                continue;
            }

            _pendingSpeed.Remove(tag.Id);
            _last[tag.Id] = (value, good, sample.Timestamp);
            buffer.Append(tag.Id, sample.Timestamp, value, good);
            written++;
        }

        return written;
    }

    /// <summary>Forwards the speeds held back whose interval has passed by <paramref name="now"/>.</summary>
    public int Flush(DateTimeOffset now)
    {
        lock (_lock)
        {
            var due = _pendingSpeed.Where(p => _last.TryGetValue(p.Key, out var last) && now - last.At >= speedInterval).ToList();
            foreach (var (tagId, (value, at)) in due)
            {
                _pendingSpeed.Remove(tagId);
                _last[tagId] = (value, true, at);
                buffer.Append(tagId, at, value, good: true);
            }

            return due.Count;
        }
    }

    public void Discovered(Guid connectionId, DiscoveredAddress address) =>
        _seen.AddOrUpdate((connectionId, address.Address),
            _ => Seen(connectionId, address.Address, address.DataType, address.Value, address.SeenAt),
            (_, before) => before with { DataType = address.DataType ?? before.DataType, Value = Text(address.Value), SeenAt = Max(before.SeenAt, address.SeenAt) });

    /// <summary>Everything seen since the last call, to report to Pulaski.</summary>
    public IReadOnlyList<EdgeMetricReportDto> TakeSeen()
    {
        var seen = new List<EdgeMetricReportDto>();
        foreach (var key in _seen.Keys)
        {
            if (_seen.TryRemove(key, out var metric))
            {
                seen.Add(metric);
            }
        }

        return seen;
    }

    /// <summary>A value as a number: numbers as they are, booleans as 0 and 1, text when it reads as a number; otherwise none.</summary>
    public static decimal? Number(object? value) => value switch
    {
        null => null,
        bool b => b ? 1 : 0,
        float f when float.IsFinite(f) => (decimal)f,
        double d when double.IsFinite(d) && Math.Abs(d) < 7.9e27 => (decimal)d,
        float or double => null,
        IConvertible c when value is not string => c.ToDecimal(CultureInfo.InvariantCulture),
        string s => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null,
        _ => null,
    };

    private static EdgeMetricReportDto Seen(Guid connectionId, string address, string? dataType, object? value, DateTimeOffset at) =>
        new() { ConnectionId = connectionId, Address = address, DataType = dataType, Value = Text(value), SeenAt = at };

    private static string? Text(object? value) => value switch
    {
        null => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}
