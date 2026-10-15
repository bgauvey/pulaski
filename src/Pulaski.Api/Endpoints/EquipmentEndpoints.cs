using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;

namespace Pulaski.Api.Endpoints;

internal static class EquipmentEndpoints
{
    public static IEndpointRouteBuilder MapEquipmentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/equipment").WithTags("Equipment");

        group.MapGet("/", async Task<Ok<IReadOnlyList<EquipmentDto>>> (
            Guid? siteId,
            IQueryHandler<ListEquipmentQuery, IReadOnlyList<EquipmentDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListEquipmentQuery(siteId), ct)));

        group.MapGet("/{id:guid}", async Task<Ok<EquipmentDto>> (
            Guid id,
            IQueryHandler<GetEquipmentQuery, EquipmentDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetEquipmentQuery(id), ct)));

        group.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateEquipmentRequest request,
            ICommandHandler<CreateEquipmentCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var command = new CreateEquipmentCommand(
                request.ParentId,
                request.Code,
                request.Name,
                request.Description,
                request.Level,
                request.EquipmentRole,
                request.TracksState,
                request.IsSchedulable,
                request.HoldsInventory,
                request.TimeZone,
                request.CanRunJob);
            var created = await handler.HandleAsync(command, ct);
            return TypedResults.Created($"/api/equipment/{created.Id}", created);
        });

        group.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateEquipmentRequest request,
            ICommandHandler<UpdateEquipmentCommand, VersionResponse> handler,
            CancellationToken ct) =>
        {
            var command = new UpdateEquipmentCommand(
                id,
                request.Version,
                request.Name,
                request.Description,
                request.TracksState,
                request.IsSchedulable,
                request.HoldsInventory,
                request.IsActive,
                request.CanRunJob);
            return TypedResults.Ok(await handler.HandleAsync(command, ct));
        });

        routes.MapGet("/equipment-types", async Task<Ok<IReadOnlyList<EquipmentTypeDto>>> (
            IQueryHandler<ListEquipmentTypesQuery, IReadOnlyList<EquipmentTypeDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListEquipmentTypesQuery(), ct)))
            .WithTags("Equipment");

        return routes;
    }
}
