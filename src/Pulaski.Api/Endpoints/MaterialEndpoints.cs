using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Materials management (D-033, D-034): materials, their import, capabilities and classes.</summary>
internal static class MaterialEndpoints
{
    /// <summary>Largest import file accepted, in bytes (characters, once read).</summary>
    private const int MaxFileBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapMaterialEndpoints(this IEndpointRouteBuilder routes)
    {
        var materials = routes.MapGroup("/materials").WithTags("Materials");

        materials.MapGet("/{id:guid}", async Task<Ok<MaterialDetailDto>> (
            Guid id,
            IQueryHandler<GetMaterialQuery, MaterialDetailDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMaterialQuery(id), ct)))
        .RequireAny(null, Permissions.MaterialsView);

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
        })
        .RequirePermission(Permissions.Materials);

        materials.MapPut("/{id:guid}", async Task<Ok<UpdateMaterialResponse>> (
            Guid id,
            UpdateMaterialRequest request,
            ICommandHandler<UpdateMaterialCommand, UpdateMaterialResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateMaterialCommand(id, request.Version, request.Code, request.Name, request.Description, request.MaterialType,
                    request.MaterialClassId, request.BaseUomId, request.IsLotTracked, request.ShelfLifeDays, request.IsActive),
                ct)))
        .RequirePermission(Permissions.Materials);

        // One item from an external item master; safe to send again (D-033).
        materials.MapPost("/import", async Task<Ok<ImportMaterialResponse>> (
            ImportMaterialRequest request,
            ICommandHandler<ImportMaterialCommand, ImportMaterialResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ImportMaterialCommand(request.ExternalSystem, request.ExternalKey, request.ExternalVersion, request.Code, request.Name,
                    request.BaseUomCode, request.MaterialType),
                ct)))
        .RequirePermission(Permissions.Materials);

        // The formats materials can be imported from (D-053).
        materials.MapGet("/import/sources", async Task<Ok<IReadOnlyList<MaterialSourceDto>>> (
            IQueryHandler<ListMaterialSourcesQuery, IReadOnlyList<MaterialSourceDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListMaterialSourcesQuery(), ct)))
        .RequireAny(null, Permissions.MaterialsView);

        // A file to start from, in the source's format (D-055).
        materials.MapGet("/import/{source}/template", (string source, IEnumerable<IMaterialSourceAdapter> adapters) =>
        {
            var adapter = adapters.FirstOrDefault(a => string.Equals(a.Name, source, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotFoundException($"No material source '{source}'.");
            return TypedResults.File(System.Text.Encoding.UTF8.GetBytes(adapter.Template), adapter.FileTypes.First(t => t.Contains('/', StringComparison.Ordinal)), adapter.TemplateFileName);
        })
        .RequireAny(null, Permissions.MaterialsView);

        // Many materials from a file read by a source adapter (csv, b2mml), all or nothing; ?dryRun=true checks them and saves
        // nothing (D-034, D-053). ?externalSystem= names the system the items belong to, for a format that doesn't say;
        // ?createClasses=true creates the classes it names that don't exist yet (D-055).
        materials.MapPost("/import/{source}", async Task<Ok<ImportMaterialsResponse>> (
            string source,
            HttpRequest http,
            bool? dryRun,
            string? externalSystem,
            bool? createClasses,
            ICommandHandler<ImportMaterialFileCommand, ImportMaterialsResponse> handler,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Body);
            var text = http.ContentLength > MaxFileBytes ? null : await reader.ReadToEndAsync(ct);
            if (text is null || text.Length > MaxFileBytes)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["file"] = [$"The file is larger than {MaxFileBytes / 1024 / 1024} MB; split it."],
                });
            }

            return TypedResults.Ok(await handler.HandleAsync(new ImportMaterialFileCommand(source, text, externalSystem, dryRun ?? false, createClasses ?? false), ct));
        })
        .RequirePermission(Permissions.Materials)
        .Accepts<string>("text/csv", "text/plain", "application/xml", "text/xml");

        // Made in batches, or continuously again with a blank size (D-036).
        materials.MapPut("/{id:guid}/batching", async Task<Ok<UpdateMaterialResponse>> (
            Guid id,
            SetBatchingRequest request,
            ICommandHandler<SetBatchingCommand, UpdateMaterialResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetBatchingCommand(id, request.Version, request.BatchSize, request.TolerancePct, request.Rule), ct)))
        .RequirePermission(Permissions.Materials);

        materials.MapPost("/{id:guid}/capabilities", async Task<Created<CapabilityResponse>> (
            Guid id,
            AddCapabilityRequest request,
            ICommandHandler<AddCapabilityCommand, CapabilityResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new AddCapabilityCommand(id, request.EquipmentId, request.IdealRatePerHour, request.RateUomId, request.StandardChangeoverMinutes,
                    request.StandardCrewSize, request.IsPreferred, request.EffectiveFrom, request.EffectiveTo, request.BatchSize, request.BatchTolerancePct),
                ct);
            return TypedResults.Created($"/api/materials/{id}", created);
        })
        .RequirePermission(Permissions.Processes, Site.FromBody<AddCapabilityRequest>(SiteOf.Equipment, r => r.EquipmentId));

        var capabilities = routes.MapGroup("/capabilities").WithTags("Materials");

        // A capabilities CSV file to start from (D-055).
        capabilities.MapGet("/import/template", () =>
            TypedResults.File(System.Text.Encoding.UTF8.GetBytes(CapabilityCsv.Template + "\r\n"), "text/csv", "capabilities.csv"))
        .RequireAny(null, Permissions.MaterialsView);

        // Capabilities in bulk from a CSV file, all or nothing; ?dryRun=true checks them and saves nothing (D-055). Each row's line
        // must be at a site where the caller may maintain processes, as for a single capability.
        capabilities.MapPost("/import/csv", async Task<Ok<ImportMaterialsResponse>> (
            HttpContext http,
            bool? dryRun,
            ICommandHandler<ImportCapabilitiesCommand, ImportMaterialsResponse> handler,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var text = http.Request.ContentLength > MaxFileBytes ? null : await reader.ReadToEndAsync(ct);
            if (text is null || text.Length > MaxFileBytes)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["file"] = [$"The file is larger than {MaxFileBytes / 1024 / 1024} MB; split it."],
                });
            }

            var sites = await Visible.SitesAsync(http, Permissions.Processes);
            return TypedResults.Ok(await handler.HandleAsync(new ImportCapabilitiesCommand(CapabilityCsv.Parse(text), dryRun ?? false, "CSV import", sites), ct));
        })
        .RequirePermission(Permissions.Processes)
        .Accepts<string>("text/csv", "text/plain");

        capabilities.MapPut("/{id:guid}", async Task<Ok<CapabilityResponse>> (
            Guid id,
            UpdateCapabilityRequest request,
            ICommandHandler<UpdateCapabilityCommand, CapabilityResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateCapabilityCommand(id, request.Version, request.IdealRatePerHour, request.RateUomId, request.StandardChangeoverMinutes,
                    request.StandardCrewSize, request.IsPreferred, request.EffectiveFrom, request.EffectiveTo, request.BatchSize, request.BatchTolerancePct),
                ct)))
        .RequirePermission(Permissions.Processes, Site.Of(SiteOf.Capability));

        capabilities.MapPost("/{id:guid}/end", async Task<Ok<CapabilityResponse>> (
            Guid id,
            EndCapabilityRequest request,
            ICommandHandler<EndCapabilityCommand, CapabilityResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EndCapabilityCommand(id, request.Version), ct)))
        .RequirePermission(Permissions.Processes, Site.Of(SiteOf.Capability));

        var classes = routes.MapGroup("/material-classes").WithTags("Materials");

        classes.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateMaterialClassRequest request,
            ICommandHandler<CreateMaterialClassCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateMaterialClassCommand(request.Code, request.Name), ct);
            return TypedResults.Created($"/api/material-classes/{created.Id}", created);
        })
        .RequirePermission(Permissions.Materials);

        classes.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateMaterialClassRequest request,
            ICommandHandler<UpdateMaterialClassCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateMaterialClassCommand(id, request.Version, request.Name, request.IsActive), ct)))
        .RequirePermission(Permissions.Materials);

        return routes;
    }
}
