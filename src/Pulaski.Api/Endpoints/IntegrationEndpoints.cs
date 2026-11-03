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
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

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

        // A feed's settings (#73): everything but the secret. Seeing them shows the full address, so it needs integrations.manage.
        integrations.MapGet("/settings", async Task<Ok<DeliveryFeedSettingsDto>> (
            string feed, IQueryHandler<GetDeliveryFeedSettingsQuery, DeliveryFeedSettingsDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetDeliveryFeedSettingsQuery(feed), ct)))
        .RequirePermission(Permissions.Integrations);

        integrations.MapPut("/settings", async Task<Ok<VersionResponse>> (
            string feed, SaveDeliveryFeedSettingsRequest request, ICommandHandler<SaveDeliveryFeedSettingsCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveDeliveryFeedSettingsCommand(
                feed, request.Version, request.Enabled, request.Url, request.EventKinds, request.IncludeFiles, request.MaxAttempts, request.TimeoutSeconds,
                request.PollSeconds, request.StartFromNow), ct)))
        .RequirePermission(Permissions.Integrations);

        // A receiver's acknowledgement (#73): no sign-in; signed with the feed's secret like Pulaski's own messages, and fresh (5 minutes).
        integrations.MapPost("/acknowledgements", [AllowSignedOut] async Task<Results<Ok<AcknowledgementResponse>, UnauthorizedHttpResult>> (
            string feed, HttpContext http, IConfiguration configuration, TimeProvider time,
            ICommandHandler<AcknowledgeDeliveryCommand, AcknowledgementResponse> handler, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = http.Request.ContentLength is > 64 * 1024 ? "" : await reader.ReadToEndAsync(ct);
            var secret = Integrations.WebhookFeedOptions.From(configuration, feed).Secret;
            if (string.IsNullOrEmpty(secret)
                || !Integrations.AcknowledgementSignature.IsValid(secret, http.Request.Headers["X-Pulaski-Timestamp"], http.Request.Headers["X-Pulaski-Signature"], body, time.GetUtcNow()))
            {
                return TypedResults.Unauthorized();
            }

            var request = System.Text.Json.JsonSerializer.Deserialize<AcknowledgementRequest>(body, Json)
                ?? new AcknowledgementRequest();
            http.Items[Infrastructure.HttpRequestContext.ActorItem] = $"receiver:{feed}";
            return TypedResults.Ok(await handler.HandleAsync(new AcknowledgeDeliveryCommand(feed, request.EventId, request.Status, request.Reference, request.Message), ct));
        });

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
