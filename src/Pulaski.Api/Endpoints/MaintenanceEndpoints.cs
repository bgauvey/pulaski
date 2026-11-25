using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Maintenance;
using Pulaski.Application.Security;
using Pulaski.Contracts.Maintenance;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Maintenance requests (#127, D-115): raised for a line or machine, on its own or from a stop, started and closed.</summary>
internal static class MaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder routes)
    {
        var maintenance = routes.MapGroup("/maintenance").WithTags("Maintenance");

        // Open requests by default; with open=false, those raised in [from, to), at most 92 days.
        maintenance.MapGet("/requests", async Task<Ok<IReadOnlyList<MaintenanceRequestDto>>> (
            Guid siteId, bool? open, DateTimeOffset? from, DateTimeOffset? to, Guid? equipmentId,
            IQueryHandler<ListMaintenanceRequestsQuery, IReadOnlyList<MaintenanceRequestDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListMaintenanceRequestsQuery(siteId, open ?? true, from, to, equipmentId), ct)))
        .RequireAny(Site.Query(), Permissions.MachinesView);

        maintenance.MapPost("/requests", async Task<Ok<MaintenanceRequestResponse>> (
            RaiseMaintenanceRequestRequest request, ICommandHandler<RaiseMaintenanceRequestCommand, MaintenanceRequestResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new RaiseMaintenanceRequestCommand(request.EquipmentId, request.Priority, request.Description, request.DowntimeId, request.DowntimeStartedAt), ct)))
        .RequirePermission(Permissions.MaintenanceRequest, Site.FromBody<RaiseMaintenanceRequestRequest>(SiteOf.Equipment, r => r.EquipmentId));

        maintenance.MapPost("/requests/{id:guid}/start", async Task<Ok<MaintenanceRequestResponse>> (
            Guid id, ICommandHandler<StartMaintenanceRequestCommand, MaintenanceRequestResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new StartMaintenanceRequestCommand(id), ct)))
        .RequirePermission(Permissions.MaintenanceWork, Site.Of(SiteOf.MaintenanceRequest));

        maintenance.MapPost("/requests/{id:guid}/close", async Task<Ok<MaintenanceRequestResponse>> (
            Guid id, CloseMaintenanceRequestRequest request, ICommandHandler<CloseMaintenanceRequestCommand, MaintenanceRequestResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CloseMaintenanceRequestCommand(id, request.Resolution), ct)))
        .RequirePermission(Permissions.MaintenanceWork, Site.Of(SiteOf.MaintenanceRequest));

        return routes;
    }
}
