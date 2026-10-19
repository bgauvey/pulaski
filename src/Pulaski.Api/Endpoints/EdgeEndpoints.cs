using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>
/// Pulaski Edge (D-041): edge nodes, their connections and tag bindings, maintained as part of equipment; and the calls the
/// edge itself makes, signed in as a service user holding <c>edge.ingest</c> at the node's site.
/// </summary>
internal static class EdgeEndpoints
{
    public static IEndpointRouteBuilder MapEdgeEndpoints(this IEndpointRouteBuilder routes)
    {
        var nodes = routes.MapGroup("/edge-nodes").WithTags("Machine data");

        nodes.MapGet("/", async Task<Ok<IReadOnlyList<EdgeNodeDto>>> (Guid siteId, IQueryHandler<ListEdgeNodesQuery, IReadOnlyList<EdgeNodeDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListEdgeNodesQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.EquipmentView);

        nodes.MapGet("/{id:guid}", async Task<Ok<EdgeNodeDetailDto>> (Guid id, IQueryHandler<GetEdgeNodeQuery, EdgeNodeDetailDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetEdgeNodeQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.EdgeNode), Permissions.EquipmentView);

        nodes.MapPost("/", async Task<Created<CreatedResponse>> (CreateEdgeNodeRequest request, ICommandHandler<CreateEdgeNodeCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateEdgeNodeCommand(request.SiteId, request.Code, request.Name, request.Description), ct);
            return TypedResults.Created($"/api/edge-nodes/{created.Id}", created);
        })
        .RequirePermission(Permissions.Equipment, Site.FromBody<CreateEdgeNodeRequest>(SiteOf.Equipment, r => r.SiteId));

        nodes.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (Guid id, UpdateEdgeNodeRequest request, ICommandHandler<UpdateEdgeNodeCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateEdgeNodeCommand(id, request.Version, request.Name, request.Description, request.IsActive), ct)))
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.EdgeNode));

        nodes.MapDelete("/{id:guid}", async Task<NoContent> (
            Guid id, ICommandHandler<DeleteEdgeNodeCommand, bool> handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new DeleteEdgeNodeCommand(id), ct);
            return TypedResults.NoContent();
        })
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.EdgeNode));

        nodes.MapPost("/{id:guid}/connections", async Task<Created<CreatedResponse>> (
            Guid id, CreateEdgeConnectionRequest request, ICommandHandler<CreateEdgeConnectionCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateEdgeConnectionCommand(id, request.Code, request.Name, request.Protocol, request.Settings), ct);
            return TypedResults.Created($"/api/edge-connections/{created.Id}", created);
        })
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.EdgeNode));

        routes.MapPut("/edge-connections/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateEdgeConnectionRequest request, ICommandHandler<UpdateEdgeConnectionCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateEdgeConnectionCommand(id, request.Version, request.Name, request.Settings, request.IsActive), ct)))
        .WithTags("Machine data")
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.EdgeConnection));

        routes.MapPost("/equipment-tags", async Task<Created<CreatedResponse>> (
            CreateEquipmentTagRequest request, ICommandHandler<CreateEquipmentTagCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateEquipmentTagCommand(
                request.EquipmentId, request.Role, request.ConnectionId, request.Address, request.ScaleFactor, request.ScaleOffset, request.CounterRollover), ct);
            return TypedResults.Created($"/api/equipment-tags/{created.Id}", created);
        })
        .WithTags("Machine data")
        .RequirePermission(Permissions.Equipment, Site.FromBody<CreateEquipmentTagRequest>(SiteOf.EdgeConnection, r => r.ConnectionId));

        routes.MapPut("/equipment-tags/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateEquipmentTagRequest request, ICommandHandler<UpdateEquipmentTagCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateEquipmentTagCommand(
                id, request.Version, request.Address, request.ScaleFactor, request.ScaleOffset, request.CounterRollover, request.IsActive), ct)))
        .WithTags("Machine data")
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.EquipmentTag));

        routes.MapDelete("/equipment-tags/{id:guid}", async Task<NoContent> (
            Guid id, ICommandHandler<DeleteEquipmentTagCommand, bool> handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new DeleteEquipmentTagCommand(id), ct);
            return TypedResults.NoContent();
        })
        .WithTags("Machine data")
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.EquipmentTag));

        // ---- The edge itself ----
        var edge = routes.MapGroup("/edge/nodes/{id:guid}").WithTags("Edge");

        edge.MapGet("/config", async Task<Ok<EdgeConfigDto>> (Guid id, IQueryHandler<GetEdgeConfigQuery, EdgeConfigDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetEdgeConfigQuery(id), ct)))
        .RequirePermission(Permissions.EdgeIngest, Site.Of(SiteOf.EdgeNode));

        edge.MapPost("/signals", async Task<Ok<EdgeSignalBatchResponse>> (
            Guid id, EdgeSignalBatchRequest request, ICommandHandler<IngestEdgeSignalsCommand, EdgeSignalBatchResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new IngestEdgeSignalsCommand(id, request.StreamId, request.AgentVersion, request.Signals), ct)))
        .RequirePermission(Permissions.EdgeIngest, Site.Of(SiteOf.EdgeNode));

        edge.MapPost("/metrics", async Task<Ok<int>> (Guid id, EdgeMetricsRequest request, ICommandHandler<ReportEdgeMetricsCommand, int> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ReportEdgeMetricsCommand(id, request.Metrics), ct)))
        .RequirePermission(Permissions.EdgeIngest, Site.Of(SiteOf.EdgeNode));

        return routes;
    }
}
