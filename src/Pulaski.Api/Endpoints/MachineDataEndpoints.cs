using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.MachineData;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Machine profiles and the machine data on equipment (D-041). Both are part of maintaining equipment.</summary>
internal static class MachineDataEndpoints
{
    /// <summary>Largest CSV file accepted, in bytes (characters, once read).</summary>
    private const int MaxCsvBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapMachineDataEndpoints(this IEndpointRouteBuilder routes)
    {
        var profiles = routes.MapGroup("/machine-profiles").WithTags("Machine data");

        profiles.MapGet("/", async Task<Ok<IReadOnlyList<MachineProfileDto>>> (
            Guid siteId, IQueryHandler<ListMachineProfilesQuery, IReadOnlyList<MachineProfileDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListMachineProfilesQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.EquipmentView);

        profiles.MapGet("/{id:guid}", async Task<Ok<MachineProfileDetailDto>> (
            Guid id, IQueryHandler<GetMachineProfileQuery, MachineProfileDetailDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMachineProfileQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.MachineProfile), Permissions.EquipmentView);

        profiles.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateMachineProfileRequest request, ICommandHandler<CreateMachineProfileCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateMachineProfileCommand(request.SiteId, request.Code, request.Name, request.Vendor, request.Model, request.Description), ct);
            return TypedResults.Created($"/api/machine-profiles/{created.Id}", created);
        })
        .RequirePermission(Permissions.Equipment, Site.FromBody<CreateMachineProfileRequest>(SiteOf.Equipment, r => r.SiteId));

        profiles.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateMachineProfileRequest request, ICommandHandler<UpdateMachineProfileCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateMachineProfileCommand(id, request.Version, request.Name, request.Vendor, request.Model, request.Description, request.IsActive), ct)))
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.MachineProfile));

        profiles.MapPut("/{id:guid}/states/{rawValue:int}", async Task<Ok<VersionResponse>> (
            Guid id, int rawValue, SetProfileStateRequest request, ICommandHandler<SetProfileStateCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SetProfileStateCommand(id, request.Version, rawValue, request.Name, request.PackMlState, request.ReasonCodeId, request.IsActive), ct)))
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.MachineProfile));

        profiles.MapPut("/{id:guid}/codes/{kind}/{rawValue:int}", async Task<Ok<VersionResponse>> (
            Guid id, string kind, int rawValue, SetProfileCodeRequest request, ICommandHandler<SetProfileCodeCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SetProfileCodeCommand(id, request.Version, kind, rawValue, request.Message, request.Category, request.ReasonCodeId, request.IsActive), ct)))
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.MachineProfile));

        profiles.MapPut("/{id:guid}/reasons", async Task<Ok<VersionResponse>> (
            Guid id, SetProfileReasonsRequest request, ICommandHandler<SetProfileReasonsCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetProfileReasonsCommand(id, request.Version, request.ReasonCodeIds), ct)))
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.MachineProfile));

        // A machine's definitions from a CSV file, all or nothing; ?dryRun=true checks them and saves nothing.
        profiles.MapPost("/{id:guid}/import/csv", async Task<Ok<ProfileImportResponse>> (
            Guid id, long version, bool? dryRun, HttpRequest http, ICommandHandler<ImportMachineProfileCommand, ProfileImportResponse> handler, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Body);
            var text = http.ContentLength > MaxCsvBytes ? null : await reader.ReadToEndAsync(ct);
            if (text is null || text.Length > MaxCsvBytes)
            {
                throw new ValidationException(new Dictionary<string, string[]> { ["file"] = [$"The file is larger than {MaxCsvBytes / 1024 / 1024} MB; split it."] });
            }

            return TypedResults.Ok(await handler.HandleAsync(new ImportMachineProfileCommand(id, version, MachineProfileCsv.Parse(text), dryRun ?? false), ct));
        })
        .Accepts<string>("text/csv", "text/plain")
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.MachineProfile));

        routes.MapPut("/equipment/{id:guid}/machine-data", async Task<Ok<VersionResponse>> (
            Guid id, SetEquipmentMachineDataRequest request, ICommandHandler<SetEquipmentMachineDataCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetEquipmentMachineDataCommand(
                id, request.Version, request.MachineProfileId, request.StateDerivation, request.ConstraintEquipmentId, request.MicroStopThresholdSeconds), ct)))
        .WithTags("Machine data")
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.Equipment));

        routes.MapPut("/equipment/{id:guid}/stop-reason-overrides/{rawValue:int}", async Task<Ok<VersionResponse>> (
            Guid id, int rawValue, SetStopReasonOverrideRequest request, ICommandHandler<SetStopReasonOverrideCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetStopReasonOverrideCommand(id, request.Version, rawValue, request.ReasonCodeId, request.IsActive), ct)))
        .WithTags("Machine data")
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.Equipment));

        return routes;
    }
}
