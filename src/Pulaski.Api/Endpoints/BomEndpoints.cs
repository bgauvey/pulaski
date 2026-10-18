using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Application.Boms;
using Pulaski.Contracts.Boms;
using Pulaski.Contracts.Common;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Bills of materials (D-025): what each material is made from, and the components planned for an order.</summary>
internal static class BomEndpoints
{
    public static IEndpointRouteBuilder MapBomEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/boms").WithTags("BOMs");

        group.MapGet("/", async Task<Ok<IReadOnlyList<BomSummaryDto>>> (
            Guid? materialId,
            IQueryHandler<ListBomsQuery, IReadOnlyList<BomSummaryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListBomsQuery(materialId), ct)))
        .RequireAnyView();

        group.MapGet("/{id:guid}", async Task<Ok<BomDto>> (
            Guid id,
            IQueryHandler<GetBomQuery, BomDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetBomQuery(id), ct)))
        .RequireAny(null, Permissions.ProcessesView);

        group.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateBomRequest request,
            ICommandHandler<CreateBomCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateBomCommand(request.MaterialId, request.ProcessId), ct);
            return TypedResults.Created($"/api/boms/{created.Id}", created);
        })
        .RequirePermission(Permissions.Processes);

        group.MapGet("/{id:guid}/versions/{versionId:guid}", async Task<Ok<BomVersionDto>> (
            Guid id,
            Guid versionId,
            IQueryHandler<GetBomVersionQuery, BomVersionDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetBomVersionQuery(id, versionId), ct)))
        .RequireAny(null, Permissions.ProcessesView);

        group.MapPut("/{id:guid}/versions/{versionId:guid}", async Task<Ok<SaveBomDraftResponse>> (
            Guid id,
            Guid versionId,
            SaveBomDraftRequest request,
            ICommandHandler<SaveBomDraftCommand, SaveBomDraftResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveBomDraftCommand(id, versionId, request.Version, request.Definition), ct)))
        .RequirePermission(Permissions.Processes);

        group.MapPost("/{id:guid}/versions/{versionId:guid}/release", async Task<Ok<VersionResponse>> (
            Guid id,
            Guid versionId,
            ReleaseBomVersionRequest request,
            ICommandHandler<ReleaseBomVersionCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ReleaseBomVersionCommand(id, versionId, request.Version, request.Note), ct)))
        .RequirePermission(Permissions.Processes);

        group.MapPost("/{id:guid}/drafts", async Task<Created<CreatedResponse>> (
            Guid id,
            ICommandHandler<StartBomDraftCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new StartBomDraftCommand(id), ct);
            return TypedResults.Created($"/api/boms/{id}/versions/{created.Id}", created);
        })
        .RequirePermission(Permissions.Processes);

        routes.MapGet("/work-orders/{id:guid}/components", async Task<Ok<IReadOnlyList<PlannedComponentDto>>> (
            Guid id,
            IQueryHandler<ListPlannedComponentsQuery, IReadOnlyList<PlannedComponentDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListPlannedComponentsQuery(id), ct)))
            .WithTags("Work orders")
            .RequireAny(Site.Of(SiteOf.WorkOrder), Permissions.WorkOrdersView, Permissions.ScheduleView, Permissions.OverviewView);

        return routes;
    }
}
