using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Handover;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Handover;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Shift handover (#124, D-113): notes and open items per line and shift.</summary>
internal static class HandoverEndpoints
{
    public static IEndpointRouteBuilder MapHandoverEndpoints(this IEndpointRouteBuilder routes)
    {
        var handover = routes.MapGroup("/handover").WithTags("Handover");

        handover.MapGet("/", async Task<Ok<HandoverDto>> (Guid siteId, Guid? lineId, IQueryHandler<GetHandoverQuery, HandoverDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetHandoverQuery(siteId, lineId), ct)))
        .RequireAny(Site.Query(), Permissions.FloorView);

        handover.MapPost("/entries", async Task<Ok<CreatedResponse>> (
            AddHandoverEntryRequest request, ICommandHandler<AddHandoverEntryCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new AddHandoverEntryCommand(request.LineId, request.Kind, request.Body), ct)))
        .RequirePermission(Permissions.Handover, Site.FromBody<AddHandoverEntryRequest>(SiteOf.Equipment, r => r.LineId));

        handover.MapPost("/entries/{id:guid}/close", async Task<Ok<VersionResponse>> (
            Guid id, CloseHandoverItemRequest request, ICommandHandler<CloseHandoverItemCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CloseHandoverItemCommand(id, request.Note), ct)))
        .RequirePermission(Permissions.Handover, Site.Of(SiteOf.HandoverEntry));

        return routes;
    }
}
