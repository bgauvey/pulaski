using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Testing;

/// <summary>In-memory alarms and process values (#26), as a test sets them.</summary>
public sealed class InMemoryMachineHistory : IMachineHistoryReadStore
{
    public List<AlarmDto> Alarms { get; } = [];

    public List<ProcessValueSeriesDto> Series { get; } = [];

    public Task<IReadOnlyList<AlarmDto>> ListAlarmsAsync(AlarmFilter filter, int take, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AlarmDto>>([.. Alarms
            .Where(a => a.RaisedAt < filter.To && (a.ClearedAt is null || a.ClearedAt > filter.From) && (!filter.ActiveOnly || a.ClearedAt is null))
            .Take(take)]);

    public Task<IReadOnlyList<ProcessValueSeriesDto>> ListProcessValuesAsync(
        Guid equipmentId, Guid? tagId, DateTimeOffset windowStart, DateTimeOffset windowEnd, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProcessValueSeriesDto>>([.. Series
            .Where(s => s.EquipmentId == equipmentId && (tagId is null || s.TagId == tagId))
            .Select(s => s with { Points = [.. s.Points.Where(p => p.At >= windowStart && p.At < windowEnd).Take(take)] })]);
}
