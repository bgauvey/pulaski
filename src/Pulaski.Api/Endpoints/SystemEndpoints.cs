using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Health;
using Pulaski.Contracts.Health;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>System health (#141, D-118): one answer for what is unhealthy and where to put it right.</summary>
internal static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/system/health", async Task<Ok<SystemHealthDto>> (IQueryHandler<GetSystemHealthQuery, SystemHealthDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetSystemHealthQuery(), ct)))
        .WithTags("System")
        .RequireAny(null, Permissions.SystemView);

        return routes;
    }
}
