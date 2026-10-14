using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Scheduling;

/// <summary>A line's timeline; <see cref="HorizonDays"/> bounds how far ahead shift windows are loaded.</summary>
public sealed record GetLineTimelineQuery(Guid LineId, int? HorizonDays = null) : IQuery<LineTimelineDto>;

public sealed record ListRunStandardsQuery(Guid LineId) : IQuery<IReadOnlyList<RunStandardDto>>;

public sealed record GetSchedulingPolicyQuery(Guid SiteId) : IQuery<SchedulingPolicyDto>;

internal sealed class GetLineTimelineHandler(ISchedulingReadStore store, TimeProvider timeProvider)
    : IQueryHandler<GetLineTimelineQuery, LineTimelineDto>
{
    public const int DefaultHorizonDays = 28;
    public const int MaxHorizonDays = 366;

    public async Task<LineTimelineDto> HandleAsync(GetLineTimelineQuery query, CancellationToken cancellationToken)
    {
        var horizon = query.HorizonDays ?? DefaultHorizonDays;
        new Validator()
            .Require(horizon is > 0 and <= MaxHorizonDays, nameof(query.HorizonDays), $"Must be between 1 and {MaxHorizonDays}.")
            .ThrowIfInvalid();

        var now = timeProvider.GetUtcNow();
        var source = await store.GetLinePlanAsync(query.LineId, now, now.AddDays(horizon), cancellationToken)
            ?? throw new NotFoundException($"Equipment {query.LineId} not found.");

        if (!source.Line.IsSchedulable)
        {
            throw new DomainRuleException($"{source.Line.Code} is not schedulable; jobs cannot be placed on it.");
        }

        var timeline = TimelineProjector.Project(TimelineMapping.ToPlan(source, now));
        return TimelineMapping.ToDto(source, timeline);
    }
}

internal sealed class ListRunStandardsHandler(ISchedulingReadStore store)
    : IQueryHandler<ListRunStandardsQuery, IReadOnlyList<RunStandardDto>>
{
    public Task<IReadOnlyList<RunStandardDto>> HandleAsync(ListRunStandardsQuery query, CancellationToken cancellationToken) =>
        store.ListRunStandardsAsync(query.LineId, cancellationToken);
}

internal sealed class GetSchedulingPolicyHandler(ISchedulingReadStore store)
    : IQueryHandler<GetSchedulingPolicyQuery, SchedulingPolicyDto>
{
    public async Task<SchedulingPolicyDto> HandleAsync(GetSchedulingPolicyQuery query, CancellationToken cancellationToken) =>
        await store.GetPolicyAsync(query.SiteId, cancellationToken)
            ?? throw new NotFoundException($"Site {query.SiteId} not found.");
}

internal static class TimelineMapping
{
    public static LinePlan ToPlan(LinePlanSource source, DateTimeOffset now)
    {
        var exceptions = source.Exceptions.Select(ToWindow);
        var calendar = source.HasShiftCalendar
            ? WorkCalendar.Create(source.Shifts.Select(ToWindow), exceptions)
            : WorkCalendar.AroundTheClock(exceptions);

        var rules = source.ChangeoverRules.Select(r => new ChangeoverRule(
            r.Id, r.EquipmentId, r.FromMaterialId, r.FromMaterialClassId, r.ToMaterialId, r.ToMaterialClassId, r.Duration));
        var standards = source.StandardChangeovers.ToDictionary(s => s.MaterialId, s => s.StandardChangeover);

        var jobs = source.Jobs.Select(j => new PlannedJob(
            j.JobId,
            j.JobType,
            j.Status,
            j.MaterialId is { } m ? new MaterialRef(m, j.MaterialClassId) : null,
            j.PlannedQty,
            j.UomId,
            j.GoodQty,
            j.StartedAt,
            j.EarliestStart,
            j.PlannedDuration,
            j.DueAt,
            j.Status == JobStatus.Firmed ? j.PlannedStart : null)).ToList();

        return new LinePlan(
            source.Line.Id,
            now,
            source.Line.ChangeoverMode,
            source.PreviousMaterial is { } p ? new MaterialRef(p.MaterialId, p.MaterialClassId) : null,
            jobs,
            [.. source.Rates.Select(r => new RunRate(r.MaterialId, r.UomId, r.RatePerHour, r.RateSource))],
            new ChangeoverMatrix(source.Line.Id, rules, standards),
            calendar);
    }

    public static LineTimelineDto ToDto(LinePlanSource source, LineTimeline timeline)
    {
        var jobs = source.Jobs.ToDictionary(j => j.JobId);
        var materialCodes = source.Jobs
            .Where(j => j.MaterialId is not null && j.MaterialCode is not null)
            .Select(j => (Id: j.MaterialId!.Value, Code: j.MaterialCode!))
            .Concat(source.PreviousMaterial is { } p ? [(p.MaterialId, p.MaterialCode)] : [])
            .DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id, x => x.Code);

        return new LineTimelineDto
        {
            LineId = source.Line.Id,
            LineCode = source.Line.Code,
            LineName = source.Line.Name,
            ChangeoverMode = source.Line.ChangeoverMode,
            ProjectedAt = timeline.ProjectedAt,
            HasShiftCalendar = source.HasShiftCalendar,
            Warnings = [.. timeline.Warnings.Select(ToDto)],
            Segments = [.. timeline.Segments.Select(s => ToDto(s, jobs[s.JobId], materialCodes))],
        };
    }

    private static TimelineSegmentDto ToDto(TimelineSegment segment, PlanJobRow job, Dictionary<Guid, string> materialCodes)
    {
        var placeholder = segment.Kind == SegmentKind.ChangeoverPlaceholder;
        return new TimelineSegmentDto
        {
            Kind = segment.Kind,
            JobId = job.JobId,
            JobNumber = job.JobNumber,
            JobType = placeholder ? JobType.Changeover : job.JobType,
            Status = placeholder ? JobStatus.Planned : job.Status,
            WorkOrderNumber = job.WorkOrderNumber,
            MaterialCode = job.MaterialCode,
            FromMaterialCode = segment.FromMaterial is { } from ? materialCodes.GetValueOrDefault(from.Id) : null,
            Start = segment.Start,
            End = segment.End,
            Work = segment.Work,
            PlannedQty = placeholder ? null : job.PlannedQty,
            RemainingQty = segment.RemainingQty,
            UomCode = placeholder ? null : job.UomCode,
            RatePerHour = segment.Rate?.RatePerHour,
            RateSource = segment.Rate?.Source,
            ChangeoverSource = segment.Changeover?.Source,
            DueAt = placeholder ? null : job.DueAt,
            EarliestStart = placeholder ? null : job.EarliestStart,
            BaselineStart = placeholder ? null : job.PlannedStart,
            BaselineEnd = placeholder ? null : job.PlannedEnd,
            IsLocked = !placeholder && job.IsLocked,
            Version = placeholder ? null : job.Version,
            Warnings = [.. segment.Warnings.Select(ToDto)],
        };
    }

    private static ScheduleWarningDto ToDto(ScheduleWarning warning) => new(warning.Code, warning.Message);

    private static TimeWindow ToWindow(WindowRow row) => new(row.StartsAt, row.EndsAt);
}
