using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Api.WorkOrders;
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
            HttpContext http,
            IQueryHandler<ListWorkOrdersQuery, IReadOnlyList<WorkOrderDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok((await handler.HandleAsync(new ListWorkOrdersQuery(siteId, status), ct)).At(await Visible.SitesAsync(http, Permissions.WorkOrdersView, Permissions.ScheduleView, Permissions.OverviewView), w => w.SiteId)))
        .RequireAny(Site.Query(), Permissions.WorkOrdersView, Permissions.ScheduleView, Permissions.OverviewView);

        // Before "/{id}" so "progress" is never read as an id.
        group.MapGet("/progress", async Task<Ok<IReadOnlyList<WorkOrderProgressDto>>> (
            Guid? equipmentId,
            HttpContext http,
            IQueryHandler<ListWorkOrderProgressQuery, IReadOnlyList<WorkOrderProgressDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok((await handler.HandleAsync(new ListWorkOrderProgressQuery(equipmentId), ct)).At(await Visible.SitesAsync(http, Permissions.WorkOrdersView, Permissions.ScheduleView, Permissions.OverviewView), w => w.Order.SiteId)))
        .RequireAny(Site.QueryOf(SiteOf.Equipment, "equipmentId"), Permissions.WorkOrdersView, Permissions.ScheduleView, Permissions.OverviewView);

        group.MapGet("/{id:guid}/shop-order.pdf", async Task<FileContentHttpResult> (
            Guid id,
            IQueryHandler<GetShopOrderQuery, ShopOrderDto> handler,
            ShopOrderPdfRenderer renderer,
            CancellationToken ct) =>
        {
            var shopOrder = await handler.HandleAsync(new GetShopOrderQuery(id), ct);
            var content = renderer.Render(shopOrder);
            var orderNumber = new string(shopOrder.Order.OrderNumber
                .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')
                .ToArray());
            return TypedResults.File(content, "application/pdf", $"shop-order-{orderNumber}.pdf");
        })
        .WithName("DownloadShopOrderPdf")
        .RequireAny(Site.Of(SiteOf.WorkOrder), Permissions.WorkOrdersView, Permissions.ScheduleView, Permissions.OverviewView);

        group.MapGet("/{id:guid}", async Task<Ok<WorkOrderDto>> (
            Guid id,
            IQueryHandler<GetWorkOrderQuery, WorkOrderDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetWorkOrderQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.WorkOrder), Permissions.WorkOrdersView, Permissions.ScheduleView, Permissions.OverviewView);

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
        // Orders from the ERP (D-066), one or many per call, each in its own transaction; rows at sites where the caller may not manage
        // work orders are refused.
        group.MapPost("/import", async Task<Ok<ImportWorkOrdersResponse>> (
            ImportWorkOrdersRequest request,
            HttpContext http,
            ICommandHandler<ImportWorkOrdersCommand, ImportWorkOrdersResponse> handler,
            CancellationToken ct) =>
        {
            var sites = await Visible.SitesAsync(http, Permissions.WorkOrderManage);
            return TypedResults.Ok(await handler.HandleAsync(new ImportWorkOrdersCommand(request.Orders, sites?.ToHashSet()), ct));
        })
        .RequirePermission(Permissions.WorkOrderManage);

        // The rule for ERP quantities that aren't whole batches (D-066): the site's default, and a material's own.
        routes.MapPut("/sites/{siteId:guid}/erp-batch-rule", async Task<Ok<VersionResponse>> (
            Guid siteId, SetErpBatchRuleRequest request, ICommandHandler<SetErpBatchRuleCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetErpBatchRuleCommand(siteId, request.Rule ?? ""), ct)))
            .WithTags("Work orders")
        .RequirePermission(Permissions.SiteSettings, Site.Route("siteId"));

        routes.MapPut("/materials/{id:guid}/erp-batch-rule", async Task<Ok<VersionResponse>> (
            Guid id, SetErpBatchRuleRequest request, ICommandHandler<SetMaterialErpBatchRuleCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetMaterialErpBatchRuleCommand(id, request.Version, string.IsNullOrWhiteSpace(request.Rule) ? null : request.Rule), ct)))
            .WithTags("Work orders")
        .RequirePermission(Permissions.Materials);

        group.MapPost("/{id:guid}/erp-change/accept", async Task<Ok<VersionResponse>> (
            Guid id, DecideErpChangeRequest request, ICommandHandler<AcceptErpChangeCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new AcceptErpChangeCommand(id, request.Version), ct)))
        .RequirePermission(Permissions.WorkOrderManage, Site.Of(SiteOf.WorkOrder));

        group.MapPost("/{id:guid}/erp-change/reject", async Task<Ok<VersionResponse>> (
            Guid id, DecideErpChangeRequest request, ICommandHandler<RejectErpChangeCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RejectErpChangeCommand(id, request.Version, request.Reason ?? ""), ct)))
        .RequirePermission(Permissions.WorkOrderManage, Site.Of(SiteOf.WorkOrder));

        group.MapPut("/{id:guid}/quantity", async Task<Ok<VersionResponse>> (
            Guid id, SetWorkOrderQuantityRequest request, ICommandHandler<SetWorkOrderQuantityCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetWorkOrderQuantityCommand(id, request.Version, request.Quantity), ct)))
        .RequirePermission(Permissions.WorkOrderManage, Site.Of(SiteOf.WorkOrder));

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
