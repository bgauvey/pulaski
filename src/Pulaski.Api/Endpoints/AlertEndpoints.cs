using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Alerts;
using Pulaski.Contracts.Alerts;
using Pulaski.Contracts.Common;

namespace Pulaski.Api.Endpoints;

/// <summary>
/// The signed-in user's own e-mail alerts (#121, D-109). Anyone with a role keeps their own; each alert needs its view permission at the site
/// when subscribed (and again when sent).
/// </summary>
internal static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder routes)
    {
        var alerts = routes.MapGroup("/me/alerts").WithTags("Alerts");

        alerts.MapGet("/", async Task<Ok<MyAlertsDto>> (HttpContext http, IQueryHandler<GetMyAlertsQuery, MyAlertsDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMyAlertsQuery(await CallerAccess.RequireAsync(http)), ct)))
        .RequireAnyView();

        alerts.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateAlertSubscriptionRequest request, HttpContext http, ICommandHandler<CreateAlertSubscriptionCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateAlertSubscriptionCommand(
                await CallerAccess.RequireAsync(http), request.SiteId, request.EquipmentId, request.Kind, request.Minutes), ct);
            return TypedResults.Created($"/api/me/alerts/{created.Id}", created);
        })
        .RequireAnyView();

        alerts.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateAlertSubscriptionRequest request, HttpContext http, ICommandHandler<UpdateAlertSubscriptionCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateAlertSubscriptionCommand(
                await CallerAccess.RequireAsync(http), id, request.Version, request.Minutes, request.IsActive), ct)))
        .RequireAnyView();

        // Everything waiting for the signed-in user to decide, at the sites where they may (#123).
        routes.MapGet("/me/inbox", async Task<Ok<Contracts.Inbox.InboxDto>> (
            HttpContext http, IQueryHandler<Application.Inbox.GetInboxQuery, Contracts.Inbox.InboxDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new Application.Inbox.GetInboxQuery(await CallerAccess.RequireAsync(http)), ct)))
        .WithTags("Inbox")
        .RequireAnyView();

        return routes;
    }
}
