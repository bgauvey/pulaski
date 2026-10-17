using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Materials;

namespace Pulaski.Api.Endpoints;

/// <summary>Materials management (D-033, D-034): materials, their import, capabilities and classes.</summary>
internal static class MaterialEndpoints
{
    /// <summary>Largest CSV file accepted, in bytes (characters, once read).</summary>
    private const int MaxCsvBytes = 5 * 1024 * 1024;

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

        // Many materials from a CSV file, all or nothing; ?dryRun=true checks them and saves nothing (D-034).
        materials.MapPost("/import/csv", async Task<Ok<ImportMaterialsResponse>> (
            HttpRequest http,
            bool? dryRun,
            ICommandHandler<ImportMaterialsCommand, ImportMaterialsResponse> handler,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Body);
            var text = http.ContentLength > MaxCsvBytes ? null : await reader.ReadToEndAsync(ct);
            if (text is null || text.Length > MaxCsvBytes)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["file"] = [$"The file is larger than {MaxCsvBytes / 1024 / 1024} MB; split it."],
                });
            }

            return TypedResults.Ok(await handler.HandleAsync(new ImportMaterialsCommand(MaterialCsv.Parse(text), dryRun ?? false, "CSV import"), ct));
        })
        .Accepts<string>("text/csv", "text/plain");

        // Made in batches, or continuously again with a blank size (D-036).
        materials.MapPut("/{id:guid}/batching", async Task<Ok<UpdateMaterialResponse>> (
            Guid id,
            SetBatchingRequest request,
            ICommandHandler<SetBatchingCommand, UpdateMaterialResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetBatchingCommand(id, request.Version, request.BatchSize, request.TolerancePct, request.Rule), ct)));

        materials.MapPost("/{id:guid}/capabilities", async Task<Created<CapabilityResponse>> (
            Guid id,
            AddCapabilityRequest request,
            ICommandHandler<AddCapabilityCommand, CapabilityResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new AddCapabilityCommand(id, request.EquipmentId, request.IdealRatePerHour, request.RateUomId, request.StandardChangeoverMinutes,
                    request.StandardCrewSize, request.IsPreferred, request.EffectiveFrom, request.EffectiveTo),
                ct);
            return TypedResults.Created($"/api/materials/{id}", created);
        });

        var capabilities = routes.MapGroup("/capabilities").WithTags("Materials");

        capabilities.MapPut("/{id:guid}", async Task<Ok<CapabilityResponse>> (
            Guid id,
            UpdateCapabilityRequest request,
            ICommandHandler<UpdateCapabilityCommand, CapabilityResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateCapabilityCommand(id, request.Version, request.IdealRatePerHour, request.RateUomId, request.StandardChangeoverMinutes,
                    request.StandardCrewSize, request.IsPreferred, request.EffectiveFrom, request.EffectiveTo),
                ct)));

        capabilities.MapPost("/{id:guid}/end", async Task<Ok<CapabilityResponse>> (
            Guid id,
            EndCapabilityRequest request,
            ICommandHandler<EndCapabilityCommand, CapabilityResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EndCapabilityCommand(id, request.Version), ct)));

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
