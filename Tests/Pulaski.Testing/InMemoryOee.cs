using Pulaski.Application.Labor;
using Pulaski.Application.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Testing;

/// <summary>In-memory machine history for the utilization and OEE report (D-052). The test adds equipment, states, stops, runs and counts.</summary>
public sealed class InMemoryOee : IOeeReadStore
{
    private readonly Lock _lock = new();
    private readonly List<OeeEquipmentRecord> _equipment = [];
    private readonly Dictionary<Guid, EquipmentShifts> _calendars = [];
    private readonly List<(Guid EquipmentId, StatePoint State)> _states = [];
    private readonly List<(Guid EquipmentId, StopSpan Stop)> _stops = [];
    private readonly List<OeeRunRecord> _runs = [];
    private readonly List<(Guid EquipmentId, OeeCountRecord Count)> _counts = [];
    private readonly List<OeeReasonRecord> _reasons = [];

    public string TimeZone { get; set; } = "UTC";

    public HashSet<int> ProductionModes { get; } = [1];

    /// <summary>Adds equipment in tree order: a parent before what is below it.</summary>
    public void AddEquipment(OeeEquipmentRecord equipment, EquipmentShifts? calendar = null)
    {
        lock (_lock)
        {
            _equipment.Add(equipment);
            if (calendar is not null)
            {
                _calendars[equipment.Id] = calendar;
            }
        }
    }

    public void AddState(Guid equipmentId, StatePoint state)
    {
        lock (_lock)
        {
            _states.Add((equipmentId, state));
        }
    }

    public void AddStop(Guid equipmentId, StopSpan stop)
    {
        lock (_lock)
        {
            _stops.Add((equipmentId, stop));
        }
    }

    public void AddRun(OeeRunRecord run)
    {
        lock (_lock)
        {
            _runs.Add(run);
        }
    }

    public void AddCount(Guid equipmentId, OeeCountRecord count)
    {
        lock (_lock)
        {
            _counts.Add((equipmentId, count));
        }
    }

    public void AddReason(OeeReasonRecord reason)
    {
        lock (_lock)
        {
            _reasons.Add(reason);
        }
    }

    public Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult<string?>(TimeZone);

    public Task<IReadOnlyList<OeeEquipmentRecord>> ListEquipmentAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OeeEquipmentRecord>>([.. _equipment]);
        }
    }

    public Task<IReadOnlySet<int>> GetProductionModesAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<int>>(ProductionModes.ToHashSet());

    public Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<EquipmentShifts>>([.. equipmentIds.Where(_calendars.ContainsKey).Select(id => _calendars[id])]);
        }
    }

    public Task<IReadOnlyList<(Guid EquipmentId, StatePoint State)>> ListStatesAsync(
        IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var mine = _states.Where(s => equipmentIds.Contains(s.EquipmentId)).ToList();
            var inside = mine.Where(s => s.State.At >= windowStart && s.State.At < windowEnd);
            var before = mine.Where(s => s.State.At < windowStart).GroupBy(s => s.EquipmentId).Select(g => g.MaxBy(s => s.State.At));
            return Task.FromResult<IReadOnlyList<(Guid, StatePoint)>>([.. inside.Concat(before)]);
        }
    }

    public Task<IReadOnlyList<(Guid EquipmentId, StopSpan Stop)>> ListStopsAsync(
        IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<(Guid, StopSpan)>>(
                [.. _stops.Where(s => equipmentIds.Contains(s.EquipmentId) && s.Stop.Start < windowEnd && (s.Stop.End ?? DateTimeOffset.MaxValue) > windowStart)]);
        }
    }

    public Task<IReadOnlyList<OeeRunRecord>> ListRunsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OeeRunRecord>>(
                [.. _runs.Where(r => equipmentIds.Contains(r.EquipmentId) && r.StartedAt < windowEnd && (r.EndedAt ?? DateTimeOffset.MaxValue) > windowStart)]);
        }
    }

    public Task<IReadOnlyList<OeeCountRecord>> ListCountsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OeeCountRecord>>(
                [.. _counts.Where(c => equipmentIds.Contains(c.EquipmentId) && c.Count.OccurredAt >= windowStart && c.Count.OccurredAt < windowEnd).Select(c => c.Count)]);
        }
    }

    public Task<IReadOnlyList<OeeReasonRecord>> ListReasonsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OeeReasonRecord>>([.. _reasons.Where(r => ids.Contains(r.Id))]);
        }
    }

    // Materials as the runs name them.
    public Task<IReadOnlyList<OeeMaterialRecord>> ListMaterialsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OeeMaterialRecord>>([.. _runs
                .Where(r => r.MaterialId is { } m && ids.Contains(m))
                .DistinctBy(r => r.MaterialId)
                .Select(r => new OeeMaterialRecord(r.MaterialId!.Value, r.MaterialCode ?? "", r.MaterialName ?? ""))]);
        }
    }
}
