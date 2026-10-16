using Pulaski.Application.Abstractions;
using Pulaski.Application.Units;
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

        if (!source.Line.IsSchedulable && !source.Line.CanRunJob)
        {
            throw new DomainRuleException($"{source.Line.Code} is not schedulable; jobs cannot be placed on it.");
        }

        if (source.Line.IsGroup)
        {
            return TimelineMapping.ToGroupDto(source, now);
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
        var calendar = Calendar(source);

        var rules = source.ChangeoverRules.Select(r => new ChangeoverRule(
            r.Id, r.EquipmentId, r.FromMaterialId, r.FromMaterialClassId, r.ToMaterialId, r.ToMaterialClassId, r.Duration));
        var standards = source.StandardChangeovers.ToDictionary(s => s.MaterialId, s => s.StandardChangeover);

        var jobs = source.Jobs.Select(j => ToPlanned(j, source)).ToList();

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
            Overlaps = [.. timeline.Overlaps.Select(o => new TimelineOverlapDto(
                o.FirstJobId, o.FirstKind, o.SecondJobId, o.SecondKind, o.Start, o.End))],
            Segments = [.. timeline.Segments.Select(s => ToDto(s, jobs[s.JobId], materialCodes, source))],
        };
    }

    private static TimelineSegmentDto ToDto(TimelineSegment segment, PlanJobRow job, Dictionary<Guid, string> materialCodes, LinePlanSource source)
    {
        var placeholder = segment.Kind == SegmentKind.ChangeoverPlaceholder;
        return new TimelineSegmentDto
        {
            Kind = segment.Kind,
            JobId = job.JobId,
            JobNumber = job.JobNumber,
            JobType = placeholder ? JobType.Changeover : job.JobType,
            Status = placeholder ? JobStatus.Planned : job.Status,
            WorkOrderId = job.WorkOrderId,
            WorkOrderNumber = job.WorkOrderNumber,
            LatestEnd = placeholder ? null : job.LatestEnd,
            GroupEquipmentId = placeholder ? null : job.GroupEquipmentId,
            ProcessOperationId = placeholder ? null : job.ProcessOperationId,
            MaterialCode = job.MaterialCode,
            FromMaterialCode = segment.FromMaterial is { } from ? materialCodes.GetValueOrDefault(from.Id) : null,
            Start = segment.Start,
            End = segment.End,
            Work = segment.Work,
            PlannedQty = placeholder ? null : job.PlannedQty,
            RemainingQty = segment.RemainingQty,
            UomCode = placeholder ? null : job.UomCode,
            RatePerHour = segment.Rate is { } rate ? InJobUnit(rate, job.UomId, source) : null,
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

    /// <summary>
    /// A group's timeline (D-022): each job as its window, checked against the lines under the
    /// group that could run it. Nothing is projected and no line is chosen.
    /// </summary>
    public static LineTimelineDto ToGroupDto(LinePlanSource source, DateTimeOffset now)
    {
        var calendar = Calendar(source);
        var segments = source.Jobs.Select(job =>
        {
            // Each line's rate is stated in the job's unit, so lines whose rate is in another unit compare (D-026).
            var runners = source.Runners
                .Where(r => r.MaterialId == job.MaterialId)
                .Select(r => new Runner(
                    r.LineId,
                    r.LineCode,
                    r.RatePerHour is { } rate && r.UomId is { } uom ? RateInJobUnit(new RunRate(r.MaterialId, uom, rate, r.RateSource ?? RateSource.Calculated), job.UomId, source) : null))
                .ToList();
            var window = GroupWindows.Evaluate(ToPlanned(job, source), runners, calendar, now);

            return new TimelineSegmentDto
            {
                Kind = SegmentKind.Window,
                JobId = job.JobId,
                JobNumber = job.JobNumber,
                JobType = job.JobType,
                Status = job.Status,
                WorkOrderId = job.WorkOrderId,
                WorkOrderNumber = job.WorkOrderNumber,
                MaterialCode = job.MaterialCode,
                Start = window.Start,
                End = window.End,
                Work = window.Fastest?.Work ?? TimeSpan.Zero,
                PlannedQty = job.PlannedQty,
                RemainingQty = Math.Max(0m, (job.PlannedQty ?? 0m) - job.GoodQty),
                UomCode = job.UomCode,
                DueAt = job.DueAt,
                EarliestStart = job.EarliestStart,
                LatestEnd = job.LatestEnd,
                GroupEquipmentId = job.GroupEquipmentId,
                ProcessOperationId = job.ProcessOperationId,
                BaselineStart = job.PlannedStart,
                BaselineEnd = job.PlannedEnd,
                IsLocked = job.IsLocked,
                Version = job.Version,
                Candidates = [.. window.Runners.Select(r => new CandidateLineDto(r.LineId, r.LineCode, r.Work, r.FitsWindow))],
                Warnings = [.. window.Warnings.Select(ToDto)],
            };
        });

        return new LineTimelineDto
        {
            LineId = source.Line.Id,
            LineCode = source.Line.Code,
            LineName = source.Line.Name,
            ChangeoverMode = source.Line.ChangeoverMode,
            IsGroup = true,
            ProjectedAt = now,
            HasShiftCalendar = source.HasShiftCalendar,
            Segments = [.. segments.OrderBy(s => s.Start).ThenBy(s => s.JobNumber, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// A firmed job is anchored at its firmed start (D-019), unless it was planned to a group: that
    /// job was firmed to a window, not a start, so on a line it runs where it is sequenced (D-022).
    /// </summary>
    /// <summary>What one of the job's unit is in its run standard's unit, when they differ and convert (D-026).</summary>
    private static decimal? ToRateUnit(Guid? materialId, Guid? jobUomId, Guid rateUomId, LinePlanSource source) =>
        materialId is { } material && jobUomId is { } uom && uom != rateUomId
            ? UnitConversions.For(material, source.Units, source.Conversions).Factor(uom, rateUomId)
            : null;

    /// <summary>The rate per hour in the job's unit, for display; the rate as it is when the units match or don't convert.</summary>
    private static decimal InJobUnit(RunRate rate, Guid? jobUomId, LinePlanSource source) =>
        ToRateUnit(rate.MaterialId, jobUomId, rate.UomId, source) is { } factor ? Math.Round(rate.RatePerHour / factor, 6) : rate.RatePerHour;

    /// <summary>The rate restated in the job's unit when they convert, so a group's lines compare whatever unit their rates use.</summary>
    private static RunRate RateInJobUnit(RunRate rate, Guid? jobUomId, LinePlanSource source) =>
        ToRateUnit(rate.MaterialId, jobUomId, rate.UomId, source) is { } factor
            ? rate with { UomId = jobUomId!.Value, RatePerHour = rate.RatePerHour / factor }
            : rate;

    private static PlannedJob ToPlanned(PlanJobRow j, LinePlanSource source) => new(
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
        j.Status == JobStatus.Firmed && j.GroupEquipmentId is null ? j.PlannedStart : null,
        j.LatestEnd,
        source.Rates.FirstOrDefault(r => r.MaterialId == j.MaterialId) is { } rate
            ? ToRateUnit(j.MaterialId, j.UomId, rate.UomId, source)
            : null);

    private static WorkCalendar Calendar(LinePlanSource source)
    {
        var exceptions = source.Exceptions.Select(ToWindow);
        return source.HasShiftCalendar
            ? WorkCalendar.Create(source.Shifts.Select(ToWindow), exceptions)
            : WorkCalendar.AroundTheClock(exceptions);
    }

    private static ScheduleWarningDto ToDto(ScheduleWarning warning) => new(warning.Code, warning.Message);

    private static TimeWindow ToWindow(WindowRow row) => new(row.StartsAt, row.EndsAt);
}
