using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Setup;
using Pulaski.Contracts.Setup;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>The first-setup checklist (#139, D-126): what a site has and lacks, and optional steps marked not needed there.</summary>
internal static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder routes)
    {
        var setup = routes.MapGroup("/setup").WithTags("Setup");

        // Without a site (none yet) the checklist starts at making one.
        setup.MapGet("/", async Task<Ok<SetupChecklistDto>> (Guid? siteId, IQueryHandler<GetSetupChecklistQuery, SetupChecklistDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetSetupChecklistQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.SiteSettingsView);

        setup.MapPut("/{siteId:guid}/steps/{step}", async Task<Ok<SetupChecklistDto>> (
            Guid siteId, string step, MarkSetupStepRequest request, ICommandHandler<MarkSetupStepCommand, SetupChecklistDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new MarkSetupStepCommand(siteId, step, request.NotNeeded), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        return routes;
    }
}
