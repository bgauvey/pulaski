using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;

namespace Pulaski.Api.Endpoints;

internal static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/scheduling").WithTags("Scheduling");

        group.MapGet("/lines/{lineId:guid}/timeline", async Task<Ok<LineTimelineDto>> (
            Guid lineId,
            int? horizonDays,
            IQueryHandler<GetLineTimelineQuery, LineTimelineDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLineTimelineQuery(lineId, horizonDays), ct)));

        return routes;
    }
}
