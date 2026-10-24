using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Integrations;
using Pulaski.Contracts.Integrations;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Outbound deliveries to the ERP (D-061): how the feed is doing, what failed, and putting it right.</summary>
internal static class IntegrationEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder routes)
    {
        var integrations = routes.MapGroup("/integrations/erp").WithTags("Integrations");

        integrations.MapGet("/", async Task<Ok<DeliveryStatusDto>> (IQueryHandler<GetDeliveryStatusQuery, DeliveryStatusDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetDeliveryStatusQuery(), ct)))
        .RequireAny(null, Permissions.IntegrationsView);

        integrations.MapGet("/deliveries", async Task<Ok<IReadOnlyList<DeliveryDto>>> (
            string? status,
            int? limit,
            IQueryHandler<ListDeliveriesQuery, IReadOnlyList<DeliveryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListDeliveriesQuery(status, limit ?? 200), ct)))
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
            ICommandHandler<RetryFailedDeliveriesCommand, RetryDeliveriesResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RetryFailedDeliveriesCommand(), ct)))
        .RequirePermission(Permissions.Integrations);

        return routes;
    }
}
