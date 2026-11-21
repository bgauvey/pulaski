using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Andon;
using Pulaski.Application.Security;
using Pulaski.Contracts.Andon;
using Pulaski.Contracts.Common;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Andon (#125, D-108): a site's call types, and calls for help raised from lines, acknowledged and resolved.</summary>
internal static class AndonEndpoints
{
    public static IEndpointRouteBuilder MapAndonEndpoints(this IEndpointRouteBuilder routes)
    {
        var andon = routes.MapGroup("/andon").WithTags("Andon");

        andon.MapGet("/types", async Task<Ok<IReadOnlyList<AndonTypeDto>>> (
            Guid siteId, IQueryHandler<ListAndonTypesQuery, IReadOnlyList<AndonTypeDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListAndonTypesQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.FloorView, Permissions.SiteSettingsView);

        andon.MapPost("/types", async Task<Created<CreatedResponse>> (
            CreateAndonTypeRequest request, ICommandHandler<CreateAndonTypeCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateAndonTypeCommand(request.SiteId, request.Code, request.Name, request.EscalateAfterMinutes, request.SortOrder), ct);
            return TypedResults.Created($"/api/andon/types/{created.Id}", created);
        })
        .RequirePermission(Permissions.SiteSettings, Site.FromBody<CreateAndonTypeRequest>(SiteOf.Equipment, r => r.SiteId));

        andon.MapPut("/types/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateAndonTypeRequest request, ICommandHandler<UpdateAndonTypeCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateAndonTypeCommand(id, request.Version, request.Name, request.EscalateAfterMinutes, request.SortOrder, request.IsActive), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Of(SiteOf.AndonType));

        // Open calls by default; with open=false, the calls raised in [from, to).
        andon.MapGet("/calls", async Task<Ok<IReadOnlyList<AndonCallDto>>> (
            Guid siteId, bool? open, DateTimeOffset? from, DateTimeOffset? to, Guid? lineId,
            IQueryHandler<ListAndonCallsQuery, IReadOnlyList<AndonCallDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListAndonCallsQuery(siteId, open ?? true, from, to, lineId), ct)))
        .RequireAny(Site.Query(), Permissions.FloorView);

        andon.MapPost("/calls", async Task<Ok<AndonCallResponse>> (
            RaiseAndonCallRequest request, ICommandHandler<RaiseAndonCallCommand, AndonCallResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RaiseAndonCallCommand(request.LineId, request.TypeId, request.Note), ct)))
        .RequirePermission(Permissions.AndonCall, Site.FromBody<RaiseAndonCallRequest>(SiteOf.Equipment, r => r.LineId));

        andon.MapPost("/calls/{id:guid}/acknowledge", async Task<Ok<AndonCallResponse>> (
            Guid id, ICommandHandler<AcknowledgeAndonCallCommand, AndonCallResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new AcknowledgeAndonCallCommand(id), ct)))
        .RequirePermission(Permissions.AndonRespond, Site.Of(SiteOf.AndonCall));

        andon.MapPost("/calls/{id:guid}/resolve", async Task<Ok<AndonCallResponse>> (
            Guid id, ResolveAndonCallRequest request, ICommandHandler<ResolveAndonCallCommand, AndonCallResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ResolveAndonCallCommand(id, request.Resolution), ct)))
        .RequirePermission(Permissions.AndonRespond, Site.Of(SiteOf.AndonCall));

        return routes;
    }
}
