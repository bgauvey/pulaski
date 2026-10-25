using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Overview;
using Pulaski.Contracts.Overview;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>The plant Overview's live figures (D-070): a summary, so seeing the Overview is enough.</summary>
internal static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/overview", async Task<Ok<OverviewDto>> (Guid siteId, IQueryHandler<GetOverviewQuery, OverviewDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetOverviewQuery(siteId), ct)))
        .WithTags("Overview")
        .RequireAny(Site.Query(), Permissions.OverviewView);

        return routes;
    }
}
