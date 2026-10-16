using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain.Processes;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.WorkOrders;

/// <summary>
/// Work orders with where they stand, for the equipment at and below <see cref="EquipmentId"/>
/// (everything when null). An order belongs to the selection when one of its open jobs is on a
/// line or group in it; an order with no open jobs belongs where it is targeted, or to its site.
/// </summary>
public sealed record ListWorkOrderProgressQuery(Guid? EquipmentId) : IQuery<IReadOnlyList<WorkOrderProgressDto>>;

/// <summary>
/// Reads each line and group in the selection through the timeline query, so every time here is
/// the same projection the schedule shows (D-014), then rolls the jobs up per work order.
/// </summary>
internal sealed class ListWorkOrderProgressHandler(
    IWorkOrderReadStore store,
    IProcessReadStore processes,
    IQueryHandler<GetLineTimelineQuery, LineTimelineDto> timelines)
    : IQueryHandler<ListWorkOrderProgressQuery, IReadOnlyList<WorkOrderProgressDto>>
{
    public async Task<IReadOnlyList<WorkOrderProgressDto>> HandleAsync(ListWorkOrderProgressQuery query, CancellationToken cancellationToken)
    {
        var scope = await store.GetScopeAsync(query.EquipmentId, cancellationToken);
        if (query.EquipmentId is { } equipmentId && scope.Count == 0)
        {
            throw new NotFoundException($"Equipment {equipmentId} not found.");
        }

        var inScope = scope.Select(e => e.Id).ToHashSet();
        var siteId = query.EquipmentId is { } selected ? scope.First(e => e.Id == selected).SiteId : null;

        var segments = new List<(LineTimelineDto Line, TimelineSegmentDto Segment)>();
        var overlapping = new HashSet<Guid>();
        foreach (var equipment in scope.Where(e => e.HoldsJobs))
        {
            var timeline = await timelines.HandleAsync(new GetLineTimelineQuery(equipment.Id), cancellationToken);
            segments.AddRange(timeline.Segments
                .Where(s => s.Kind != "changeover_placeholder" && s.WorkOrderId is not null)
                .Select(s => (timeline, s)));
            overlapping.UnionWith(timeline.Overlaps.SelectMany(o => new[] { o.FirstJobId, o.SecondJobId }));
        }

        var jobsByOrder = segments.ToLookup(x => x.Segment.WorkOrderId!.Value);
        var facts = (await store.GetFactsAsync(siteId, cancellationToken)).ToDictionary(f => f.WorkOrderId);
        var orders = await store.ListAsync(siteId, null, cancellationToken);

        var shown = orders
            .Where(o => jobsByOrder.Contains(o.Id) || (facts.GetValueOrDefault(o.Id)?.OpenJobs ?? 0) == 0 && Targets(o, inScope, query.EquipmentId))
            .ToList();

        // Each process version once: its operations decide how much of an order is on lines (D-024).
        var definitions = new Dictionary<Guid, ProcessDefinition?>();
        foreach (var order in shown.Where(o => o.ProcessId is not null && o.ProcessVersionId is not null))
        {
            if (!definitions.ContainsKey(order.ProcessVersionId!.Value))
            {
                var version = await processes.GetVersionAsync(order.ProcessId!.Value, order.ProcessVersionId.Value, cancellationToken);
                definitions[order.ProcessVersionId.Value] = version is null ? null : ProcessRules.ToDomain(version.Definition);
            }
        }

        return [.. shown.Select(o => Progress(
            WorkOrderMapping.WithNextStatuses(o),
            jobsByOrder[o.Id],
            facts.GetValueOrDefault(o.Id),
            overlapping,
            o.ProcessVersionId is { } versionId ? definitions.GetValueOrDefault(versionId) : null))];
    }

    /// <summary>An order without open jobs belongs to its target equipment, or to its site when it has none.</summary>
    private static bool Targets(WorkOrderDto order, HashSet<Guid> inScope, Guid? selected) =>
        selected is null || inScope.Contains(order.TargetEquipmentId ?? order.SiteId);

    private static WorkOrderProgressDto Progress(
        WorkOrderDto order,
        IEnumerable<(LineTimelineDto Line, TimelineSegmentDto Segment)> jobs,
        WorkOrderFactsRow? facts,
        HashSet<Guid> overlapping,
        ProcessDefinition? definition)
    {
        var open = jobs.OrderBy(j => j.Segment.Start).ThenBy(j => j.Segment.JobNumber, StringComparer.Ordinal).ToList();
        DateTimeOffset? start = open.Count > 0 ? open.Min(j => j.Segment.Start) : null;
        DateTimeOffset? end = open.Count > 0 ? open.Max(j => j.Segment.End) : null;
        var baselineStart = open.Select(j => j.Segment.BaselineStart).Where(t => t is not null).Min();
        var baselineEnd = open.Select(j => j.Segment.BaselineEnd).Where(t => t is not null).Max();

        return new WorkOrderProgressDto
        {
            Order = order,
            Jobs = [.. open.Select(j => new WorkOrderJobDto(
                j.Segment.JobId, j.Segment.JobNumber, j.Segment.Kind, j.Segment.Status, j.Line.LineId, j.Line.LineCode,
                j.Segment.Start, j.Segment.End, j.Segment.PlannedQty, j.Segment.RemainingQty, j.Segment.Warnings))],
            ProjectedStart = start,
            ProjectedEnd = end,
            BaselineStart = baselineStart,
            BaselineEnd = baselineEnd,
            StartSlip = Later(start, baselineStart),
            FinishSlip = Later(end, order.DueAt),
            CompletedQty = facts?.CompletedQty ?? 0m,
            PlacedQty = OperationQuantities.Placed(definition, open.Select(j => (j.Segment.ProcessOperationId, j.Segment.PlannedQty ?? 0m))),
            IsRunning = open.Any(j => j.Segment.Status == "running"),
            Warnings = [.. open.SelectMany(j => j.Segment.Warnings).DistinctBy(w => w.Code)],
            HasOverlap = open.Any(j => overlapping.Contains(j.Segment.JobId)),
        };
    }

    private static TimeSpan? Later(DateTimeOffset? actual, DateTimeOffset? reference) =>
        actual is { } a && reference is { } r && a > r ? a - r : null;
}
