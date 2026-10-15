using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Scheduling;

/// <summary>
/// Where a new production job for <see cref="PlannedQty"/> of a work order would run if it were
/// put on the line before <see cref="BeforeJobId"/> (null: at the end). A read: nothing is saved.
/// </summary>
public sealed record PreviewJobPlacementQuery(
    Guid LineId,
    Guid WorkOrderId,
    decimal PlannedQty,
    Guid? BeforeJobId,
    string? JobNumber = null) : IQuery<JobPlacementPreviewDto>;

/// <summary>
/// Projects the line twice, as it is and with the would-be job in its slot, so the planner sees
/// where the job lands, how long it takes with its changeover, and what it pushes back, before
/// committing to it. It refuses what creating the job would refuse, with the same rules (D-018, D-020).
/// </summary>
internal sealed class PreviewJobPlacementHandler(
    ISchedulingReadStore store,
    IWorkOrderReadStore workOrders,
    IIdGenerator ids,
    TimeProvider timeProvider) : IQueryHandler<PreviewJobPlacementQuery, JobPlacementPreviewDto>
{
    public async Task<JobPlacementPreviewDto> HandleAsync(PreviewJobPlacementQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.LineId != Guid.Empty, nameof(query.LineId), "Required.")
            .Require(query.WorkOrderId != Guid.Empty, nameof(query.WorkOrderId), "Required.")
            .Require(query.PlannedQty > 0, nameof(query.PlannedQty), "Must be greater than zero.")
            .ThrowIfInvalid();

        var now = timeProvider.GetUtcNow();
        var source = await store.GetLinePlanAsync(
                query.LineId, now, now.AddDays(GetLineTimelineHandler.DefaultHorizonDays), cancellationToken)
            ?? throw new NotFoundException($"Equipment {query.LineId} not found.");
        if (!source.Line.IsSchedulable)
        {
            throw new DomainRuleException($"{source.Line.Code} is not schedulable; jobs cannot be placed on it.");
        }

        var workOrder = await workOrders.GetAsync(query.WorkOrderId, cancellationToken)
            ?? throw new NotFoundException($"Work order {query.WorkOrderId} not found.");
        var check = await store.GetPlacementCheckAsync(source.Line.Id, workOrder.MaterialId, now, cancellationToken)
            ?? throw new NotFoundException($"Equipment {query.LineId} not found.");

        if (check.LineSiteId != workOrder.SiteId)
        {
            throw new DomainRuleException($"Work order {workOrder.OrderNumber} belongs to another site than {source.Line.Code}.");
        }

        if (workOrder.Status != WorkOrderStatus.Planned)
        {
            throw new DomainRuleException($"Work order {workOrder.OrderNumber} is {workOrder.Status}; only planned orders get new jobs.");
        }

        if (!check.IsEligible)
        {
            throw new DomainRuleException($"{workOrder.MaterialCode} is not set up to run on {source.Line.Code}; add a capability for it first.");
        }

        var slot = query.BeforeJobId is { } before
            ? source.Jobs.ToList().FindIndex(j => j.JobId == before)
            : source.Jobs.Count;
        if (slot < 0)
        {
            throw new DomainRuleException($"Job {query.BeforeJobId} is not an open job on {source.Line.Code}.");
        }

        var preview = new PlanJobRow
        {
            JobId = ids.NewId(),
            JobNumber = string.IsNullOrWhiteSpace(query.JobNumber) ? workOrder.OrderNumber : query.JobNumber.Trim(),
            JobType = JobType.Production,
            Status = JobStatus.Planned,
            MaterialId = workOrder.MaterialId,
            MaterialCode = workOrder.MaterialCode,
            MaterialClassId = check.MaterialClassId,
            PlannedQty = query.PlannedQty,
            UomId = workOrder.UomId,
            UomCode = workOrder.UomCode,
            WorkOrderId = workOrder.Id,
            WorkOrderNumber = workOrder.OrderNumber,
            DueAt = workOrder.DueAt,
        };

        var jobs = source.Jobs.ToList();
        jobs.Insert(slot, preview);
        var withJob = source with { Jobs = jobs };

        var current = TimelineProjector.Project(TimelineMapping.ToPlan(source, now));
        var proposed = TimelineProjector.Project(TimelineMapping.ToPlan(withJob, now));

        var startsNow = current.Segments.Where(s => s.Kind == SegmentKind.Job).ToDictionary(s => s.JobId, s => s.Start);
        var numbers = source.Jobs.ToDictionary(j => j.JobId, j => j.JobNumber);
        var delayed = proposed.Segments
            .Where(s => s.Kind == SegmentKind.Job && startsNow.TryGetValue(s.JobId, out var was) && s.Start > was)
            .Select(s => new JobDelayDto(s.JobId, numbers[s.JobId], s.Start - startsNow[s.JobId]))
            .ToList();

        return new JobPlacementPreviewDto
        {
            PreviewJobId = preview.JobId,
            Timeline = TimelineMapping.ToDto(withJob, proposed),
            Delayed = delayed,
        };
    }
}
