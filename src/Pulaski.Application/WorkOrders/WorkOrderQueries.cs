using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.WorkOrders;

public sealed record ListWorkOrdersQuery(Guid? SiteId, string? Status) : IQuery<IReadOnlyList<WorkOrderDto>>;

public sealed record GetWorkOrderQuery(Guid Id) : IQuery<WorkOrderDto>;

internal sealed class ListWorkOrdersHandler(IWorkOrderReadStore store)
    : IQueryHandler<ListWorkOrdersQuery, IReadOnlyList<WorkOrderDto>>
{
    public async Task<IReadOnlyList<WorkOrderDto>> HandleAsync(ListWorkOrdersQuery query, CancellationToken cancellationToken)
    {
        if (query.Status is not null && !WorkOrderStatus.All.Contains(query.Status))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(query.Status)] = [$"Unknown status '{query.Status}'."],
            });
        }

        var rows = await store.ListAsync(query.SiteId, query.Status, cancellationToken);
        return [.. rows.Select(WorkOrderMapping.WithNextStatuses)];
    }
}

internal sealed class GetWorkOrderHandler(IWorkOrderReadStore store)
    : IQueryHandler<GetWorkOrderQuery, WorkOrderDto>
{
    public async Task<WorkOrderDto> HandleAsync(GetWorkOrderQuery query, CancellationToken cancellationToken)
    {
        var row = await store.GetAsync(query.Id, cancellationToken)
            ?? throw new NotFoundException($"Work order {query.Id} not found.");
        return WorkOrderMapping.WithNextStatuses(row);
    }
}

internal static class WorkOrderMapping
{
    public static WorkOrderDto WithNextStatuses(WorkOrderDto row) =>
        row with { NextStatuses = WorkOrderStatus.NextStatuses(row.Status) };
}
