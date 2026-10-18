using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Application.Processes;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Process routes (D-023): reusable templates of operations that orders will be made from.</summary>
internal static class ProcessEndpoints
{
    public static IEndpointRouteBuilder MapProcessEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/processes").WithTags("Processes");

        // With materialId: only the processes whose scope covers it, as order creation will offer them.
        group.MapGet("/", async Task<Ok<IReadOnlyList<ProcessSummaryDto>>> (
            Guid? siteId,
            Guid? materialId,
            IQueryHandler<ListProcessesQuery, IReadOnlyList<ProcessSummaryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListProcessesQuery(siteId, materialId), ct)));

        // Whether an order for the material can be made from the process, as creating it would check (D-024).
        group.MapGet("/{id:guid}/order-check", async Task<Ok<ProcessOrderCheckDto>> (
            Guid id,
            Guid materialId,
            IQueryHandler<CheckProcessForOrderQuery, ProcessOrderCheckDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CheckProcessForOrderQuery(id, materialId), ct)));

        group.MapPut("/{id:guid}/scope", async Task<Ok<VersionResponse>> (
            Guid id,
            SetProcessScopeRequest request,
            ICommandHandler<SetProcessScopeCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SetProcessScopeCommand(id, request.Version, request.AllMaterials, request.MaterialClassIds, request.MaterialIds), ct)))
        .RequirePermission(Permissions.Processes, Site.Of(SiteOf.Process));

        group.MapGet("/{id:guid}", async Task<Ok<ProcessDto>> (
            Guid id,
            IQueryHandler<GetProcessQuery, ProcessDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetProcessQuery(id), ct)));

        group.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateProcessRequest request,
            ICommandHandler<CreateProcessCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateProcessCommand(request.SiteId, request.Code, request.Name, request.Description), ct);
            return TypedResults.Created($"/api/processes/{created.Id}", created);
        })
        .RequirePermission(Permissions.Processes, Site.FromBody<CreateProcessRequest>(SiteOf.Equipment, r => r.SiteId));

        group.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateProcessRequest request,
            ICommandHandler<UpdateProcessCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateProcessCommand(id, request.Version, request.Name, request.Description, request.IsActive), ct)))
        .RequirePermission(Permissions.Processes, Site.Of(SiteOf.Process));

        group.MapGet("/{id:guid}/versions/{versionId:guid}", async Task<Ok<ProcessVersionDto>> (
            Guid id,
            Guid versionId,
            IQueryHandler<GetProcessVersionQuery, ProcessVersionDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetProcessVersionQuery(id, versionId), ct)));

        group.MapPut("/{id:guid}/versions/{versionId:guid}", async Task<Ok<SaveProcessDraftResponse>> (
            Guid id,
            Guid versionId,
            SaveProcessDraftRequest request,
            ICommandHandler<SaveProcessDraftCommand, SaveProcessDraftResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SaveProcessDraftCommand(id, versionId, request.Version, request.Definition), ct)))
        .RequirePermission(Permissions.Processes, Site.Of(SiteOf.Process));

        group.MapPost("/{id:guid}/versions/{versionId:guid}/release", async Task<Ok<ReleaseProcessVersionResponse>> (
            Guid id,
            Guid versionId,
            ReleaseProcessVersionRequest request,
            ICommandHandler<ReleaseProcessVersionCommand, ReleaseProcessVersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ReleaseProcessVersionCommand(id, versionId, request.Version, request.Note), ct)))
        .RequirePermission(Permissions.Processes, Site.Of(SiteOf.Process));

        group.MapPost("/{id:guid}/drafts", async Task<Created<CreatedResponse>> (
            Guid id,
            ICommandHandler<StartProcessDraftCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new StartProcessDraftCommand(id), ct);
            return TypedResults.Created($"/api/processes/{id}/versions/{created.Id}", created);
        })
        .RequirePermission(Permissions.Processes, Site.Of(SiteOf.Process));

        routes.MapPut("/materials/{id:guid}/default-process", async Task<Ok<VersionResponse>> (
            Guid id,
            SetDefaultProcessRequest request,
            ICommandHandler<SetDefaultProcessCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetDefaultProcessCommand(id, request.ProcessId), ct)))
        .RequirePermission(Permissions.Processes)
            .WithTags("Processes");

        return routes;
    }
}
