using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Api.Endpoints;

internal static class WorkOrderEndpoints
{
    public static IEndpointRouteBuilder MapWorkOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/work-orders").WithTags("Work orders");

        group.MapGet("/", async Task<Ok<IReadOnlyList<WorkOrderDto>>> (
            Guid? siteId,
            string? status,
            IQueryHandler<ListWorkOrdersQuery, IReadOnlyList<WorkOrderDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListWorkOrdersQuery(siteId, status), ct)));

        group.MapGet("/{id:guid}", async Task<Ok<WorkOrderDto>> (
            Guid id,
            IQueryHandler<GetWorkOrderQuery, WorkOrderDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetWorkOrderQuery(id), ct)));

        group.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateWorkOrderRequest request,
            ICommandHandler<CreateWorkOrderCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var command = new CreateWorkOrderCommand(
                request.SiteId,
                request.OrderNumber,
                request.MaterialId,
                request.PlannedQty,
                request.UomId,
                request.TargetEquipmentId,
                request.PlannedStart,
                request.PlannedEnd,
                request.DueAt,
                request.Priority);
            var created = await handler.HandleAsync(command, ct);
            return TypedResults.Created($"/api/work-orders/{created.Id}", created);
        });

        group.MapPost("/{id:guid}/status", async Task<Ok<VersionResponse>> (
            Guid id,
            ChangeWorkOrderStatusRequest request,
            ICommandHandler<ChangeWorkOrderStatusCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ChangeWorkOrderStatusCommand(id, request.Version, request.Status, request.Reason), ct)));

        return routes;
    }
}
