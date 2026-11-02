using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Integrations;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Integrations;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Outbound deliveries to the ERP and a document system (D-061, #78): how each feed is doing, what failed, and putting it right.</summary>
internal static class IntegrationEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder routes)
    {
        // One group per feed (D-061, #78): /integrations/erp and /integrations/documents.
        var integrations = routes.MapGroup("/integrations/{feed:regex(^(erp|documents)$)}").WithTags("Integrations");

        integrations.MapGet("/", async Task<Ok<DeliveryStatusDto>> (string feed, IQueryHandler<GetDeliveryStatusQuery, DeliveryStatusDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetDeliveryStatusQuery(feed), ct)))
        .RequireAny(null, Permissions.IntegrationsView);

        integrations.MapGet("/deliveries", async Task<Ok<IReadOnlyList<DeliveryDto>>> (
            string feed,
            string? status,
            int? limit,
            IQueryHandler<ListDeliveriesQuery, IReadOnlyList<DeliveryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListDeliveriesQuery(status, limit ?? 200, feed), ct)))
        .RequireAny(null, Permissions.IntegrationsView);

        integrations.MapPost("/deliveries/{id:guid}/retry", async Task<Ok<RetryDeliveriesResponse>> (
            Guid id, ICommandHandler<RetryDeliveryCommand, RetryDeliveriesResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RetryDeliveryCommand(id), ct)))
        .RequirePermission(Permissions.Integrations);

        integrations.MapPost("/deliveries/{id:guid}/skip", async Task<Ok<RetryDeliveriesResponse>> (
            Guid id, SkipDeliveryRequest request, ICommandHandler<SkipDeliveryCommand, RetryDeliveriesResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SkipDeliveryCommand(id, request.Reason), ct)))
        .RequirePermission(Permissions.Integrations);

        integrations.MapPost("/deliveries/retry-failed", async Task<Ok<RetryDeliveriesResponse>> (
            string feed, ICommandHandler<RetryFailedDeliveriesCommand, RetryDeliveriesResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RetryFailedDeliveriesCommand(feed), ct)))
        .RequirePermission(Permissions.Integrations);

        // External sources of material master data (D-068): set up by whoever manages integrations, run by whoever manages materials.
        var connections = routes.MapGroup("/material-connections").WithTags("Integrations");

        connections.MapGet("/", async Task<Ok<IReadOnlyList<MaterialConnectionDto>>> (
            IQueryHandler<ListMaterialConnectionsQuery, IReadOnlyList<MaterialConnectionDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListMaterialConnectionsQuery(), ct)))
        .RequireAny(null, Permissions.IntegrationsView, Permissions.MaterialsView);

        connections.MapPost("/", async Task<Ok<CreatedResponse>> (
            SaveMaterialConnectionRequest request, ICommandHandler<SaveMaterialConnectionCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveMaterialConnectionCommand(null, request), ct)))
        .RequirePermission(Permissions.Integrations);

        connections.MapPut("/{id:guid}", async Task<Ok<CreatedResponse>> (
            Guid id, SaveMaterialConnectionRequest request, ICommandHandler<SaveMaterialConnectionCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveMaterialConnectionCommand(id, request), ct)))
        .RequirePermission(Permissions.Integrations);

        connections.MapPost("/{id:guid}/run", async Task<Ok<ImportMaterialsResponse>> (
            Guid id, RunMaterialConnectionRequest request, ICommandHandler<RunMaterialConnectionCommand, ImportMaterialsResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RunMaterialConnectionCommand(id, request.DryRun, request.CreateClasses), ct)))
        .RequirePermission(Permissions.Materials);

        return routes;
    }
}
