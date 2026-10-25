using Pulaski.Application.MachineData;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Testing;

/// <summary>
/// In-memory stops to code and the machine status board (D-050), and stops to adjust (D-069). The test adds stops, reasons and status
/// rows; stops to adjust and their equipment are added on their own.
/// </summary>
public sealed class InMemoryStops : IStopRepository, IStopReadStore, IStopAdjustmentRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, StopRecord> _stops = [];
    private readonly Dictionary<Guid, List<ReasonCodeDto>> _reasons = [];
    private readonly List<MachineStatusRow> _status = [];
    private readonly Dictionary<Guid, StopEquipment> _equipment = [];
    private readonly Dictionary<Guid, Guid> _runs = [];

    /// <summary>Stops to adjust, with those adjustments wrote; superseded ones stay.</summary>
    public Dictionary<Guid, AdjustableStop> Adjustable { get; } = [];

    /// <summary>The stops adjustments wrote, in order.</summary>
    public List<NewStop> Inserted { get; } = [];

    public Dictionary<Guid, (Guid ReasonCodeId, string? Comment, string CodedBy)> Coded { get; } = [];

    public void AddStop(StopRecord stop)
    {
        lock (_lock)
        {
            _stops[stop.Id] = stop;
        }
    }

    public void AddReason(Guid equipmentId, ReasonCodeDto reason)
    {
        lock (_lock)
        {
            (_reasons.TryGetValue(equipmentId, out var list) ? list : _reasons[equipmentId] = []).Add(reason);
        }
    }

    public void AddStatus(MachineStatusDto status, Guid? constraint = null)
    {
        lock (_lock)
        {
            _status.Add(new MachineStatusRow(status, constraint));
        }
    }

    public Task<StopRecord?> GetStopForUpdateAsync(Guid id, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_stops.TryGetValue(id, out var s) && s.StartedAt == startedAt ? s : null);
        }
    }

    public Task<IReadOnlyList<CodingReason>> ListCodingReasonsAsync(Guid equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<CodingReason>>([.. _reasons.GetValueOrDefault(equipmentId, []).Select(r => new CodingReason(r.Id, r.RequiresComment))]);
        }
    }

    public Task CodeStopAsync(Guid id, DateTimeOffset startedAt, Guid reasonCodeId, string? comment, string codedBy, Guid? codedByUserId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Coded[id] = (reasonCodeId, comment, codedBy);
            _stops[id] = _stops[id] with { OperatorReasonCodeId = reasonCodeId, Comment = comment };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MachineStatusRow>> ListMachineStatusAsync(Guid siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MachineStatusRow>>([.. _status]);
        }
    }

    public Task<IReadOnlyList<DowntimeDto>> ListStopsAsync(StopFilter filter, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DowntimeDto>>([]);

    Task<IReadOnlyList<ReasonCodeDto>> IStopReadStore.ListCodingReasonsAsync(Guid equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ReasonCodeDto>>([.. _reasons.GetValueOrDefault(equipmentId, [])]);
        }
    }

    public void AddEquipment(StopEquipment equipment, Guid? openRun = null)
    {
        lock (_lock)
        {
            _equipment[equipment.Id] = equipment;
            if (openRun is { } run)
            {
                _runs[equipment.Id] = run;
            }
        }
    }

    public void AddAdjustable(AdjustableStop stop)
    {
        lock (_lock)
        {
            Adjustable[stop.Id] = stop;
        }
    }

    public Task<StopEquipment?> LockEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_equipment.GetValueOrDefault(equipmentId));
        }
    }

    public Task<IReadOnlyList<Guid>> EquipmentOfStopsAsync(IReadOnlyList<StopKeyDto> stops, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Guid>>([.. Find(stops).Select(s => s.EquipmentId).Distinct()]);
        }
    }

    public Task<IReadOnlyList<AdjustableStop>> GetStopsForUpdateAsync(IReadOnlyList<StopKeyDto> stops, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AdjustableStop>>([.. Find(stops)]);
        }
    }

    public Task<IReadOnlyList<StopInterval>> ListCurrentStopsAsync(Guid equipmentId, DateTimeOffset start, DateTimeOffset until, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<StopInterval>>([.. Adjustable.Values
                .Where(s => s.EquipmentId == equipmentId && !s.IsSuperseded && s.StartedAt < until && (s.EndedAt is not { } e || e > start))
                .Select(s => s.Interval)]);
        }
    }

    public Task<Guid?> RunAtAsync(Guid equipmentId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_runs.TryGetValue(equipmentId, out var run) ? run : (Guid?)null);
        }
    }

    public Task InsertAsync(NewStop newStop, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Inserted.Add(newStop);
            Adjustable[newStop.Id] = new AdjustableStop(newStop.Id, newStop.StartedAt, newStop.EndedAt, newStop.EquipmentId, newStop.RunId, newStop.PackMlState,
                newStop.UnitMode, newStop.PlcStopReason, newStop.MappedReasonCodeId, newStop.OperatorReasonCodeId, newStop.Comment, newStop.CodedBy,
                newStop.CodedByUserId, newStop.CodedAt, newStop.Source, IsSuperseded: false);
        }

        return Task.CompletedTask;
    }

    public Task SupersedeAsync(IReadOnlyList<StopKeyDto> stops, string supersededBy, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var stop in Find(stops))
            {
                Adjustable[stop.Id] = stop with { IsSuperseded = true };
            }
        }

        return Task.CompletedTask;
    }

    private List<AdjustableStop> Find(IReadOnlyList<StopKeyDto> keys) =>
        [.. keys.Select(k => Adjustable.TryGetValue(k.Id, out var s) && s.StartedAt == k.StartedAt ? s : null).OfType<AdjustableStop>()];
}
