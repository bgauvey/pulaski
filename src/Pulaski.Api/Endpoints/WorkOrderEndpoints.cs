using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Application.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain.Security;

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

        // Before "/{id}" so "progress" is never read as an id.
        group.MapGet("/progress", async Task<Ok<IReadOnlyList<WorkOrderProgressDto>>> (
            Guid? equipmentId,
            IQueryHandler<ListWorkOrderProgressQuery, IReadOnlyList<WorkOrderProgressDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListWorkOrderProgressQuery(equipmentId), ct)));

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
                request.ProcessId,
                request.OrderNumber,
                request.MaterialId,
                request.PlannedQty,
                request.UomId,
                request.PlannedStart,
                request.DueAt,
                request.Priority,
                request.Notes);
            var created = await handler.HandleAsync(command, ct);
            return TypedResults.Created($"/api/work-orders/{created.Id}", created);
        })
        .RequirePermission(Permissions.WorkOrderManage, Site.FromBody<CreateWorkOrderRequest>(SiteOf.Process, r => r.ProcessId));

        group.MapPost("/{id:guid}/status", async Task<Ok<VersionResponse>> (
            Guid id,
            ChangeWorkOrderStatusRequest request,
            ICommandHandler<ChangeWorkOrderStatusCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ChangeWorkOrderStatusCommand(id, request.Version, request.Status, request.Reason), ct)))
        .RequirePermission(Permissions.WorkOrderClose, Site.Of(SiteOf.WorkOrder));

        // Planning (D-018): these also carry the order's jobs, so they are not plain status changes.
        group.MapPost("/{id:guid}/dispatch", async Task<Ok<DispatchWorkOrderResponse>> (
            Guid id,
            WorkOrderPlanningRequest request,
            ICommandHandler<DispatchWorkOrderCommand, DispatchWorkOrderResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new DispatchWorkOrderCommand(id, request.Version, request.Reason), ct)))
        .RequirePermission(Permissions.WorkOrderManage, Site.Of(SiteOf.WorkOrder));

        group.MapPost("/{id:guid}/undispatch", async Task<Ok<VersionResponse>> (
            Guid id,
            WorkOrderPlanningRequest request,
            ICommandHandler<UndispatchWorkOrderCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UndispatchWorkOrderCommand(id, request.Version, request.Reason), ct)))
        .RequirePermission(Permissions.WorkOrderManage, Site.Of(SiteOf.WorkOrder));

        group.MapPost("/{id:guid}/firm", async Task<Ok<VersionResponse>> (
            Guid id,
            WorkOrderPlanningRequest request,
            ICommandHandler<FirmWorkOrderCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new FirmWorkOrderCommand(id, request.Version, request.Reason), ct)))
        .RequirePermission(Permissions.WorkOrderManage, Site.Of(SiteOf.WorkOrder));

        return routes;
    }
}
