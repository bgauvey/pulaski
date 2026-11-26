using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Quality;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Nonconformance reports (#133, D-124): raised from anything that flags, investigated, acted on and closed.</summary>
internal static class NcrEndpoints
{
    public static IEndpointRouteBuilder MapNcrEndpoints(this IEndpointRouteBuilder routes)
    {
        var ncrs = routes.MapGroup("/ncrs").WithTags("Quality");

        ncrs.MapGet("/", async Task<Ok<IReadOnlyList<NcrSummaryDto>>> (
            Guid? siteId, string? status, Guid? lotId, HttpContext http, IQueryHandler<ListNcrsQuery, IReadOnlyList<NcrSummaryDto>> handler, CancellationToken ct) =>
        {
            var sites = await Visible.SitesAsync(http, Permissions.QualityView, Permissions.InventoryView);
            return TypedResults.Ok((await handler.HandleAsync(new ListNcrsQuery(siteId, status, lotId), ct)).At(sites, n => n.SiteId));
        })
        .RequireAnyAtSite(Site.Query(), Permissions.QualityView, Permissions.InventoryView);

        ncrs.MapGet("/{id:guid}", async Task<Ok<NcrDto>> (Guid id, IQueryHandler<GetNcrQuery, NcrDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetNcrQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.Nonconformance), Permissions.QualityView, Permissions.InventoryView);

        ncrs.MapGet("/assignees", async Task<Ok<IReadOnlyList<NcrAssigneeDto>>> (
            Guid siteId, IQueryHandler<ListNcrAssigneesQuery, IReadOnlyList<NcrAssigneeDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListNcrAssigneesQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.NcrRaise, Permissions.NcrManage);

        // The site comes from the source, so the permission is checked at any site here and at the NCR's own in the handler.
        ncrs.MapPost("/", async Task<Ok<NcrCreatedResponse>> (
            RaiseNcrRequest request, HttpContext http, ICommandHandler<RaiseNcrCommand, NcrCreatedResponse> handler, CancellationToken ct) =>
        {
            var sites = await Visible.SitesAsync(http, Permissions.NcrRaise);
            return TypedResults.Ok(await handler.HandleAsync(new RaiseNcrCommand(
                request.SiteId, request.Title, request.Description, request.Severity, request.SourceKind, request.SourceId, request.SourceAt, request.OwnerUserId,
                request.LotIds, sites), ct));
        })
        .RequirePermission(Permissions.NcrRaise);

        ncrs.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (Guid id, UpdateNcrRequest request, ICommandHandler<UpdateNcrCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateNcrCommand(id, request.Version, request.Title, request.Description, request.Severity, request.OwnerUserId, request.AddLotIds), ct)))
        .RequirePermission(Permissions.NcrManage, Site.Of(SiteOf.Nonconformance));

        ncrs.MapPost("/{id:guid}/lots", async Task<Ok<VersionResponse>> (Guid id, LinkNcrLotRequest request, ICommandHandler<LinkNcrLotCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new LinkNcrLotCommand(id, request.LotNumber), ct)))
        .RequirePermission(Permissions.NcrManage, Site.Of(SiteOf.Nonconformance));

        ncrs.MapPut("/{id:guid}/investigation", async Task<Ok<VersionResponse>> (
            Guid id, InvestigateNcrRequest request, ICommandHandler<InvestigateNcrCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new InvestigateNcrCommand(id, request.Version, request.RootCause, request.Whys, request.Complete), ct)))
        .RequirePermission(Permissions.NcrManage, Site.Of(SiteOf.Nonconformance));

        ncrs.MapPost("/{id:guid}/actions", async Task<Ok<CreatedResponse>> (
            Guid id, AddNcrActionRequest request, ICommandHandler<AddNcrActionCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new AddNcrActionCommand(id, request.Kind, request.Description, request.OwnerUserId, request.DueOn), ct)))
        .RequirePermission(Permissions.NcrManage, Site.Of(SiteOf.Nonconformance));

        // An action's owner finishes it whatever their permissions; others need to manage NCRs at the site (checked in the handler).
        ncrs.MapPost("/{id:guid}/actions/{actionId:guid}/finish", async Task<Ok<VersionResponse>> (
            Guid id, Guid actionId, FinishNcrActionRequest request, HttpContext http, ISiteLookup lookup,
            ICommandHandler<FinishNcrActionCommand, VersionResponse> handler, CancellationToken ct) =>
        {
            var access = await CallerAccess.RequireAsync(http);
            var site = await lookup.SiteOfAsync(SiteOf.Nonconformance, id, ct);
            var mayManage = site is not null && access.Can(Permissions.NcrManage, site);
            return TypedResults.Ok(await handler.HandleAsync(new FinishNcrActionCommand(id, actionId, request.Note, request.Cancel, mayManage), ct));
        });

        ncrs.MapPost("/{id:guid}/close", async Task<Ok<VersionResponse>> (Guid id, CloseNcrRequest request, ICommandHandler<CloseNcrCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CloseNcrCommand(id, request.Version, request.Disposition, request.Note), ct)))
        .RequirePermission(Permissions.NcrManage, Site.Of(SiteOf.Nonconformance));

        return routes;
    }
}
