using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Units of measure and each material's own conversions (D-026). The unit list is <c>GET /api/uoms</c>.</summary>
internal static class UnitEndpoints
{
    public static IEndpointRouteBuilder MapUnitEndpoints(this IEndpointRouteBuilder routes)
    {
        var uoms = routes.MapGroup("/uoms").WithTags("Units");

        uoms.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateUomRequest request,
            ICommandHandler<CreateUnitCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateUnitCommand(request.Code, request.Name, request.Dimension, request.ToBaseFactor), ct);
            return TypedResults.Created($"/api/uoms/{created.Id}", created);
        })
        .RequirePermission(Permissions.Units);

        uoms.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateUomRequest request,
            ICommandHandler<UpdateUnitCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateUnitCommand(id, request.Version, request.Name, request.IsActive), ct)))
        .RequirePermission(Permissions.Units);

        // With materialId, the material's own conversions apply as well as the standard factors.
        uoms.MapGet("/convert", async Task<Ok<UnitConversionResultDto>> (
            decimal quantity,
            Guid fromUomId,
            Guid toUomId,
            Guid? materialId,
            IQueryHandler<ConvertQuantityQuery, UnitConversionResultDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ConvertQuantityQuery(quantity, fromUomId, toUomId, materialId), ct)))
        .RequireAnyView();

        var conversions = routes.MapGroup("/materials/{materialId:guid}/conversions").WithTags("Units");

        conversions.MapGet("/", async Task<Ok<IReadOnlyList<MaterialConversionDto>>> (
            Guid materialId,
            IQueryHandler<ListMaterialConversionsQuery, IReadOnlyList<MaterialConversionDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListMaterialConversionsQuery(materialId), ct)))
        .RequireAnyView();

        conversions.MapPut("/", async Task<Ok<MaterialConversionDto>> (
            Guid materialId,
            SetMaterialConversionRequest request,
            ICommandHandler<SetMaterialConversionCommand, MaterialConversionDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SetMaterialConversionCommand(materialId, request.FromUomId, request.ToUomId, request.Factor), ct)))
        .RequirePermission(Permissions.Materials);

        // Switches the conversion off; Pulaski never deletes rows.
        conversions.MapDelete("/", async Task<NoContent> (
            Guid materialId,
            Guid fromUomId,
            Guid toUomId,
            ICommandHandler<RemoveMaterialConversionCommand, bool> handler,
            CancellationToken ct) =>
        {
            await handler.HandleAsync(new RemoveMaterialConversionCommand(materialId, fromUomId, toUomId), ct);
            return TypedResults.NoContent();
        })
        .RequirePermission(Permissions.Materials);

        return routes;
    }
}
