using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Application.WorkOrders;

public sealed record GetShopOrderQuery(Guid Id) : IQuery<ShopOrderDto>;

internal sealed class GetShopOrderHandler(
    IWorkOrderReadStore workOrders,
    IBomReadStore boms,
    IQueryHandler<GetLineTimelineQuery, LineTimelineDto> timelines)
    : IQueryHandler<GetShopOrderQuery, ShopOrderDto>
{
    public async Task<ShopOrderDto> HandleAsync(GetShopOrderQuery query, CancellationToken cancellationToken)
    {
        var order = await workOrders.GetAsync(query.Id, cancellationToken)
            ?? throw new NotFoundException($"Work order {query.Id} not found.");
        var source = await workOrders.GetShopOrderJobsAsync(query.Id, cancellationToken)
            ?? throw new NotFoundException($"Work order {query.Id} not found.");
        var components = (await boms.ListPlannedComponentsAsync(query.Id, cancellationToken))
            .ToLookup(c => c.JobId);

        var jobs = new List<ShopOrderJobDto>(source.Jobs.Count);
        var timelinesByEquipment = new Dictionary<Guid, LineTimelineDto>();
        foreach (var job in source.Jobs)
        {
            TimelineSegmentDto? segment = null;
            if (IsOpen(job.Status))
            {
                if (!timelinesByEquipment.TryGetValue(job.EquipmentId, out var timeline))
                {
                    timeline = await timelines.HandleAsync(new GetLineTimelineQuery(job.EquipmentId), cancellationToken);
                    timelinesByEquipment.Add(job.EquipmentId, timeline);
                }

                segment = timeline.Segments.FirstOrDefault(s =>
                    s.JobId == job.JobId && s.Kind is "job" or "window");
            }

            var waitingForLine = segment?.Kind == "window";
            var operation = job.OperationNumber is { } operationNumber
                ? $"OP-{operationNumber}"
                : "OP-NA";
            var barcodeValue = $"{order.OrderNumber}|{operation}";
            EnsureCode128Value(barcodeValue);

            jobs.Add(new ShopOrderJobDto
            {
                JobId = job.JobId,
                JobNumber = job.JobNumber,
                JobType = job.JobType,
                Status = job.Status,
                EquipmentId = job.EquipmentId,
                EquipmentCode = job.EquipmentCode,
                IsGroup = job.IsGroup,
                WaitingForLineAssignment = waitingForLine,
                CandidateLines = segment?.Candidates.Select(c => c.LineCode).ToArray() ?? [],
                PlannedQty = job.PlannedQty,
                UomCode = job.UomCode,
                OperationNumber = job.OperationNumber,
                OperationName = job.OperationName,
                OperationDescription = job.OperationDescription,
                ScheduledStart = segment?.Start ?? job.BaselineStart,
                ScheduledEnd = segment?.End ?? job.BaselineEnd,
                UsesProjectedSchedule = segment is not null,
                RatePerHour = segment?.RatePerHour ?? job.RatePerHour,
                RateUomCode = segment is not null ? job.UomCode : job.RateUomCode,
                RateSource = segment?.RateSource ?? job.RateSource,
                BarcodeValue = barcodeValue,
                Components = [.. components[job.JobId]],
            });
        }

        return new ShopOrderDto
        {
            Order = WorkOrderMapping.WithNextStatuses(order),
            SiteTimeZone = source.SiteTimeZone,
            Jobs = jobs,
        };
    }

    private static bool IsOpen(string status) =>
        status is "planned" or "dispatched" or "firmed" or "running" or "paused";

    private static void EnsureCode128Value(string value)
    {
        if (value.Any(c => c is < ' ' or > '~') || value.Count(c => c == '|') != 1)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["orderNumber"] = ["The order number must use printable ASCII characters and cannot contain '|' for a Code 128 shop-order barcode."],
            });
        }
    }
}
