using Pulaski.Application.MachineData;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Testing;

/// <summary>In-memory stops to code and the machine status board (D-050). The test adds stops, reasons and status rows.</summary>
public sealed class InMemoryStops : IStopRepository, IStopReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, StopRecord> _stops = [];
    private readonly Dictionary<Guid, List<ReasonCodeDto>> _reasons = [];
    private readonly List<MachineStatusRow> _status = [];

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
}
