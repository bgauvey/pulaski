using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Materials;

namespace Pulaski.Api.Endpoints;

/// <summary>Materials management (D-033): materials, their import from an external item master, and classes.</summary>
internal static class MaterialEndpoints
{
    public static IEndpointRouteBuilder MapMaterialEndpoints(this IEndpointRouteBuilder routes)
    {
        var materials = routes.MapGroup("/materials").WithTags("Materials");

        materials.MapGet("/{id:guid}", async Task<Ok<MaterialDetailDto>> (
            Guid id,
            IQueryHandler<GetMaterialQuery, MaterialDetailDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMaterialQuery(id), ct)));

        materials.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateMaterialRequest request,
            ICommandHandler<CreateMaterialCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateMaterialCommand(request.Code, request.Name, request.Description, request.MaterialType, request.MaterialClassId,
                    request.BaseUomId, request.IsLotTracked, request.ShelfLifeDays),
                ct);
            return TypedResults.Created($"/api/materials/{created.Id}", created);
        });

        materials.MapPut("/{id:guid}", async Task<Ok<UpdateMaterialResponse>> (
            Guid id,
            UpdateMaterialRequest request,
            ICommandHandler<UpdateMaterialCommand, UpdateMaterialResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateMaterialCommand(id, request.Version, request.Code, request.Name, request.Description, request.MaterialType,
                    request.MaterialClassId, request.BaseUomId, request.IsLotTracked, request.ShelfLifeDays, request.IsActive),
                ct)));

        // One item from an external item master; safe to send again (D-033).
        materials.MapPost("/import", async Task<Ok<ImportMaterialResponse>> (
            ImportMaterialRequest request,
            ICommandHandler<ImportMaterialCommand, ImportMaterialResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ImportMaterialCommand(request.ExternalSystem, request.ExternalKey, request.ExternalVersion, request.Code, request.Name,
                    request.BaseUomCode, request.MaterialType),
                ct)));

        var classes = routes.MapGroup("/material-classes").WithTags("Materials");

        classes.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateMaterialClassRequest request,
            ICommandHandler<CreateMaterialClassCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateMaterialClassCommand(request.Code, request.Name), ct);
            return TypedResults.Created($"/api/material-classes/{created.Id}", created);
        });

        classes.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateMaterialClassRequest request,
            ICommandHandler<UpdateMaterialClassCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateMaterialClassCommand(id, request.Version, request.Name, request.IsActive), ct)));

        return routes;
    }
}
