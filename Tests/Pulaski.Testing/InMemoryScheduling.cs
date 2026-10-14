using Pulaski.Application.Scheduling;

namespace Pulaski.Testing;

/// <summary>Line plan sources keyed by line id, returned as-is to the timeline query.</summary>
public sealed class InMemoryScheduling : ISchedulingReadStore
{
    private readonly Dictionary<Guid, LinePlanSource> _lines = [];

    public LinePlanSource Add(LinePlanSource source)
    {
        lock (_lines)
        {
            _lines[source.Line.Id] = source;
        }

        return source;
    }

    public Task<LinePlanSource?> GetLinePlanAsync(
        Guid lineId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        lock (_lines)
        {
            return Task.FromResult(_lines.GetValueOrDefault(lineId));
        }
    }
}
