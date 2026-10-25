using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>Alarms active in <c>[From, To)</c> (#26): raised before it ends and not cleared before it starts; only active ones when asked.</summary>
public sealed record AlarmFilter(Guid SiteId, Guid? EquipmentId, DateTimeOffset From, DateTimeOffset To, bool ActiveOnly);

/// <summary>Read side for alarms, warnings and process values (#26).</summary>
public interface IMachineHistoryReadStore
{
    /// <summary>Up to <paramref name="take"/> alarms, active first, then newest first.</summary>
    Task<IReadOnlyList<AlarmDto>> ListAlarmsAsync(AlarmFilter filter, int take, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The equipment's process value and level tags (one, with <paramref name="tagId"/>), each with up to <paramref name="take"/> readings in <c>[windowStart, windowEnd)</c>.</summary>
    Task<IReadOnlyList<ProcessValueSeriesDto>> ListProcessValuesAsync(
        Guid equipmentId, Guid? tagId, DateTimeOffset windowStart, DateTimeOffset windowEnd, int take, CancellationToken cancellationToken);
}

public sealed record ListAlarmsQuery(AlarmFilter Filter) : IQuery<AlarmListDto>;

public sealed record GetProcessValuesQuery(Guid EquipmentId, Guid? TagId, DateTimeOffset From, DateTimeOffset To) : IQuery<IReadOnlyList<ProcessValueSeriesDto>>;

internal sealed class ListAlarmsHandler(IMachineHistoryReadStore store, TimeProvider time) : IQueryHandler<ListAlarmsQuery, AlarmListDto>
{
    public const int MaxAlarms = 2000;

    public async Task<AlarmListDto> HandleAsync(ListAlarmsQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.Filter.To > query.Filter.From, "To", "After From.")
            .Require(query.Filter.To - query.Filter.From <= TimeSpan.FromDays(31), "To", "At most 31 days at a time.")
            .ThrowIfInvalid();

        // One more than the limit says whether there were more, so the answer never looks complete when it isn't.
        var rows = await store.ListAlarmsAsync(query.Filter, MaxAlarms + 1, time.GetUtcNow(), cancellationToken);
        return new AlarmListDto { Alarms = [.. rows.Take(MaxAlarms)], IsTruncated = rows.Count > MaxAlarms };
    }
}

internal sealed class GetProcessValuesHandler(IMachineHistoryReadStore store) : IQueryHandler<GetProcessValuesQuery, IReadOnlyList<ProcessValueSeriesDto>>
{
    public const int MaxPoints = 10_000;

    public async Task<IReadOnlyList<ProcessValueSeriesDto>> HandleAsync(GetProcessValuesQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.To > query.From, "To", "After From.")
            .Require(query.To - query.From <= TimeSpan.FromDays(31), "To", "At most 31 days at a time.")
            .ThrowIfInvalid();

        var series = await store.ListProcessValuesAsync(query.EquipmentId, query.TagId, query.From, query.To, MaxPoints + 1, cancellationToken);
        return [.. series.Select(s => s.Points.Count > MaxPoints ? s with { Points = [.. s.Points.Take(MaxPoints)], IsTruncated = true } : s)];
    }
}
