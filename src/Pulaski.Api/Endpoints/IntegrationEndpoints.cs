using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Integrations;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Integrations;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.Materials;
using Pulaski.Application.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Outbound deliveries to the ERP and a document system (D-061, #78): how each feed is doing, what failed, and putting it right.</summary>
internal static class IntegrationEndpoints
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder routes)
    {
        // The feeds (#74): the built-in erp and documents, and those added on the screen.
        routes.MapGet("/integrations", async Task<Ok<IReadOnlyList<DeliveryFeedSummaryDto>>> (
            IQueryHandler<ListDeliveryFeedsQuery, IReadOnlyList<DeliveryFeedSummaryDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListDeliveryFeedsQuery(), ct)))
            .WithTags("Integrations")
        .RequireAny(null, Permissions.IntegrationsView);

        routes.MapPost("/integrations", async Task<Ok<VersionResponse>> (
            CreateDeliveryFeedRequest request, ICommandHandler<CreateDeliveryFeedCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CreateDeliveryFeedCommand(request.Code, request.Name, request.Kind), ct)))
            .WithTags("Integrations")
        .RequirePermission(Permissions.Integrations);

        // One group per feed (D-061, #74): /integrations/{code}.
        var integrations = routes.MapGroup("/integrations/{feed:regex(^[a-z][a-z0-9_-]{{1,29}}$)}").WithTags("Integrations");

        integrations.MapGet("/", async Task<Ok<DeliveryStatusDto>> (string feed, IQueryHandler<GetDeliveryStatusQuery, DeliveryStatusDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetDeliveryStatusQuery(feed), ct)))
        .RequireAny(null, Permissions.IntegrationsView);

        integrations.MapGet("/deliveries", async Task<Ok<IReadOnlyList<DeliveryDto>>> (
            string feed,
            string? status,
            string? search,
            string? type,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            IQueryHandler<ListDeliveriesQuery, IReadOnlyList<DeliveryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListDeliveriesQuery(status, limit ?? 200, feed) { Search = search, Type = type, From = from, To = to }, ct)))
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
                request.PollSeconds, request.StartFromNow, request.Name, request.Topic), ct)))
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

        // Its imports, on demand and scheduled, newest first (#71).
        connections.MapGet("/{id:guid}/runs", async Task<Ok<IReadOnlyList<ConnectionRunDto>>> (
            Guid id, int? limit, IQueryHandler<ListConnectionRunsQuery, IReadOnlyList<ConnectionRunDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListConnectionRunsQuery(id, ConnectionTargets.Materials, limit ?? 50), ct)))
        .RequireAny(null, Permissions.IntegrationsView, Permissions.MaterialsView);

        // Sources of people at a site (#71): set up by whoever manages integrations there, run by whoever maintains its people.
        var people = routes.MapGroup("/people-connections").WithTags("Integrations");

        people.MapGet("/", async Task<Ok<IReadOnlyList<MaterialConnectionDto>>> (
            Guid siteId, IQueryHandler<ListMaterialConnectionsQuery, IReadOnlyList<MaterialConnectionDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListMaterialConnectionsQuery(ConnectionTargets.People, siteId), ct)))
        .RequireAny(Site.Query(), Permissions.IntegrationsView, Permissions.LaborView);

        people.MapPost("/", async Task<Ok<CreatedResponse>> (
            SaveMaterialConnectionRequest request, ICommandHandler<SaveMaterialConnectionCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveMaterialConnectionCommand(null, request, ConnectionTargets.People), ct)))
        .RequirePermission(Permissions.Integrations, Site.FromBody<SaveMaterialConnectionRequest>(SiteOf.Equipment, r => r.SiteId));

        people.MapPut("/{id:guid}", async Task<Ok<CreatedResponse>> (
            Guid id, SaveMaterialConnectionRequest request, ICommandHandler<SaveMaterialConnectionCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveMaterialConnectionCommand(id, request, ConnectionTargets.People), ct)))
        .RequirePermission(Permissions.Integrations, Site.Of(SiteOf.Connection));

        people.MapPost("/{id:guid}/run", async Task<Ok<ImportPeopleResponse>> (
            Guid id, RunPeopleConnectionRequest request, ICommandHandler<RunPeopleConnectionCommand, ImportPeopleResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RunPeopleConnectionCommand(id, request.DryRun), ct)))
        .RequirePermission(Permissions.People, Site.Of(SiteOf.Connection));

        people.MapGet("/{id:guid}/runs", async Task<Ok<IReadOnlyList<ConnectionRunDto>>> (
            Guid id, int? limit, IQueryHandler<ListConnectionRunsQuery, IReadOnlyList<ConnectionRunDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListConnectionRunsQuery(id, ConnectionTargets.People, limit ?? 50), ct)))
        .RequireAny(Site.Of(SiteOf.Connection), Permissions.IntegrationsView, Permissions.LaborView);

        return routes;
    }
}
