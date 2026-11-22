using Pulaski.Application.Execution;
using Pulaski.Application.Labor;
using Pulaski.Domain.Inventory;

namespace Pulaski.Testing;

/// <summary>Lots filled by reports (D-104) for handler tests: each line's rule and open lot as set here; off unless set.</summary>
public sealed class InMemoryLotFilling : ILotFillingStore
{
    public Dictionary<(Guid Line, Guid Material), LotCutRule> Rules { get; } = [];

    public Dictionary<(Guid Job, Guid Line, Guid Location), OpenLot> OpenLots { get; } = [];

    public List<string> Closed { get; } = [];

    public Task<LotCutRule> GetRuleAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(Rules.GetValueOrDefault((lineId, materialId)) ?? LotCutRule.Off);

    public Task<OpenLot?> GetOpenLotForUpdateAsync(Guid jobId, Guid lineId, Guid locationId, CancellationToken cancellationToken) =>
        Task.FromResult(OpenLots.GetValueOrDefault((jobId, lineId, locationId)));

    public Task<EquipmentShifts?> GetShiftsAsync(Guid lineId, CancellationToken cancellationToken) => Task.FromResult<EquipmentShifts?>(null);

    public Task<IReadOnlyList<ClosedLot>> CloseOpenLotsAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var closed = OpenLots.Where(o => o.Key.Job == jobId).Select(o => new ClosedLot(o.Value.LotId, o.Value.LotNumber)).OrderBy(l => l.LotNumber, StringComparer.Ordinal).ToList();
        foreach (var key in OpenLots.Keys.Where(k => k.Job == jobId).ToList())
        {
            OpenLots.Remove(key);
        }

        Closed.AddRange(closed.Select(l => l.LotNumber));
        return Task.FromResult<IReadOnlyList<ClosedLot>>(closed);
    }
}
