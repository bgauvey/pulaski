using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Shifts;

namespace Pulaski.Application.Shifts;

public sealed record ListShiftSchedulesQuery(Guid? SiteId) : IQuery<IReadOnlyList<ShiftScheduleDto>>;

public sealed record GetShiftScheduleQuery(Guid Id) : IQuery<ShiftScheduleDto>;

/// <summary>What a schedule, saved or not, gives over the next days, in the site's time zone (D-038).</summary>
public sealed record PreviewShiftScheduleQuery(PreviewShiftScheduleRequest Request) : IQuery<ShiftPreviewDto>;

internal sealed class ListShiftSchedulesHandler(IShiftReadStore store) : IQueryHandler<ListShiftSchedulesQuery, IReadOnlyList<ShiftScheduleDto>>
{
    public Task<IReadOnlyList<ShiftScheduleDto>> HandleAsync(ListShiftSchedulesQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.SiteId, cancellationToken);
}

internal sealed class GetShiftScheduleHandler(IShiftReadStore store) : IQueryHandler<GetShiftScheduleQuery, ShiftScheduleDto>
{
    public async Task<ShiftScheduleDto> HandleAsync(GetShiftScheduleQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Shift schedule {query.Id} not found.");
}

internal sealed class PreviewShiftScheduleHandler(IShiftReadStore store, TimeProvider timeProvider) : IQueryHandler<PreviewShiftScheduleQuery, ShiftPreviewDto>
{
    public const int MaxDays = 62;

    public async Task<ShiftPreviewDto> HandleAsync(PreviewShiftScheduleQuery query, CancellationToken cancellationToken)
    {
        var request = query.Request;
        var timeZone = await store.GetSiteTimeZoneAsync(request.SiteId, cancellationToken) ?? throw new NotFoundException($"Site {request.SiteId} not found.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        if (!request.Shifts.All(ShiftMapping.HasValidStart))
        {
            return new ShiftPreviewDto { Problems = ["Give each shift a start time as HH:mm."], TimeZone = timeZone };
        }

        var schedule = ShiftMapping.ToDomain("PREVIEW", "Preview", request.CycleDays, request.CycleStart == default ? Today(zone) : request.CycleStart, request.Shifts);
        var problems = schedule.Problems();
        if (problems.Count > 0)
        {
            return new ShiftPreviewDto { Problems = problems, TimeZone = timeZone };
        }

        var from = request.From ?? Today(zone);
        var start = Instant(from, zone);
        var end = Instant(from.AddDays(Math.Clamp(request.Days, 1, MaxDays)), zone);
        return new ShiftPreviewDto
        {
            Shifts = [.. schedule.Expand(zone, start, end)
                .Where(o => o.Start >= start)
                .Select(o => new ShiftOccurrenceDto(o.Code, o.Name, o.ProductionDate, TimeZoneInfo.ConvertTime(o.Start, zone), TimeZoneInfo.ConvertTime(o.End, zone)))],
            HoursPerCycle = ShiftMapping.Hours(schedule.HoursPerCycle),
            TimeZone = timeZone,
        };
    }

    private DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);

    private static DateTimeOffset Instant(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
