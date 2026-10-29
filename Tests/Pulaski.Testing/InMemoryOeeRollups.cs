using Pulaski.Application.MachineData;

namespace Pulaski.Testing;

/// <summary>In-memory daily OEE rollups (#66): days rolled per site and each piece of equipment's windows of a day.</summary>
public sealed class InMemoryOeeRollups : IOeeRollupStore, IOeeRollupRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid SiteId, DateOnly Day), (long Generation, DateTimeOffset? DirtyAt)> _days = [];
    private readonly Dictionary<(Guid EquipmentId, DateOnly Day), IReadOnlyList<OeeWindowFigures>> _figures = [];
    private readonly Dictionary<Guid, long> _generations = [];

    /// <summary>Marks a rolled day as changed, as a late write to its history does.</summary>
    public void Mark(Guid siteId, DateOnly day, DateTimeOffset at)
    {
        lock (_lock)
        {
            if (_days.TryGetValue((siteId, day), out var rolled))
            {
                _days[(siteId, day)] = rolled with { DirtyAt = at };
            }
        }
    }

    /// <summary>Moves the site to a new generation, as a change to its settings does.</summary>
    public void SiteChanged(Guid siteId)
    {
        lock (_lock)
        {
            _generations[siteId] = _generations.GetValueOrDefault(siteId) + 1;
        }
    }

    public long GenerationOf(Guid siteId)
    {
        lock (_lock)
        {
            return _generations.GetValueOrDefault(siteId);
        }
    }

    public Task<IReadOnlySet<DateOnly>> ListCurrentDaysAsync(Guid siteId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var generation = _generations.GetValueOrDefault(siteId);
            return Task.FromResult<IReadOnlySet<DateOnly>>(_days
                .Where(d => d.Key.SiteId == siteId && d.Key.Day >= firstDay && d.Key.Day <= lastDay && d.Value.DirtyAt is null && d.Value.Generation == generation)
                .Select(d => d.Key.Day)
                .ToHashSet());
        }
    }

    public Task<IReadOnlyList<OeeWindowFigures>> ListFiguresAsync(IReadOnlyCollection<Guid> equipmentIds, IReadOnlyCollection<DateOnly> days, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OeeWindowFigures>>([.. _figures
                .Where(f => equipmentIds.Contains(f.Key.EquipmentId) && days.Contains(f.Key.Day))
                .SelectMany(f => f.Value)]);
        }
    }

    /// <summary>Not tracked in memory: tests roll the days they need with <see cref="OeeRollups.RollDayAsync"/>.</summary>
    public Task<IReadOnlyList<OeeDayToRoll>> ListDaysToRollAsync(DateTimeOffset now, int keepDays, int limit, Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OeeDayToRoll>>([]);

    public Task SaveDayAsync(OeeDayToRoll day, IReadOnlyCollection<Guid> equipmentIds, IReadOnlyList<OeeWindowFigures> figures, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var id in equipmentIds)
            {
                _figures[(id, day.ProductionDate)] = [.. figures.Where(f => f.EquipmentId == id)];
            }

            var dirty = _days.TryGetValue((day.SiteId, day.ProductionDate), out var seen) && seen.DirtyAt != day.DirtyAt ? seen.DirtyAt : null;
            _days[(day.SiteId, day.ProductionDate)] = (day.Generation, dirty);
            return Task.CompletedTask;
        }
    }
}
