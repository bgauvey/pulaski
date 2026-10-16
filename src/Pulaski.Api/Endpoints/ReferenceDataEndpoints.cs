using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.ReferenceData;
using Pulaski.Contracts.ReferenceData;

namespace Pulaski.Api.Endpoints;

internal static class ReferenceDataEndpoints
{
    public static IEndpointRouteBuilder MapReferenceDataEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/uoms", async Task<Ok<IReadOnlyList<UomDto>>> (
            IQueryHandler<ListUomsQuery, IReadOnlyList<UomDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListUomsQuery(), ct)))
            .WithTags("Reference data");

        routes.MapGet("/materials", async Task<Ok<IReadOnlyList<MaterialDto>>> (
            bool? includeInactive,
            IQueryHandler<ListMaterialsQuery, IReadOnlyList<MaterialDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListMaterialsQuery(includeInactive ?? false), ct)))
            .WithTags("Reference data");

        routes.MapGet("/material-classes", async Task<Ok<IReadOnlyList<MaterialClassDto>>> (
            IQueryHandler<ListMaterialClassesQuery, IReadOnlyList<MaterialClassDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListMaterialClassesQuery(), ct)))
            .WithTags("Reference data");

        return routes;
    }
}
