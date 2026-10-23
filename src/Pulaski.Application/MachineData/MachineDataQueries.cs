using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Application.MachineData;

public sealed record ListMachineProfilesQuery(Guid SiteId) : IQuery<IReadOnlyList<MachineProfileDto>>;

public sealed record GetMachineProfileQuery(Guid Id) : IQuery<MachineProfileDetailDto>;

internal sealed class ListMachineProfilesHandler(IMachineDataReadStore store) : IQueryHandler<ListMachineProfilesQuery, IReadOnlyList<MachineProfileDto>>
{
    public Task<IReadOnlyList<MachineProfileDto>> HandleAsync(ListMachineProfilesQuery query, CancellationToken cancellationToken) =>
        store.ListProfilesAsync(query.SiteId, cancellationToken);
}

internal sealed class GetMachineProfileHandler(IMachineDataReadStore store) : IQueryHandler<GetMachineProfileQuery, MachineProfileDetailDto>
{
    public async Task<MachineProfileDetailDto> HandleAsync(GetMachineProfileQuery query, CancellationToken cancellationToken) =>
        await store.GetProfileAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Machine profile {query.Id} not found.");
}

public sealed record ListEdgeNodesQuery(Guid SiteId) : IQuery<IReadOnlyList<EdgeNodeDto>>;

public sealed record GetEdgeNodeQuery(Guid Id) : IQuery<EdgeNodeDetailDto>;

/// <summary>What an edge node reads (D-050), for the edge itself.</summary>
public sealed record GetEdgeConfigQuery(Guid Id) : IQuery<EdgeConfigDto>;

internal sealed class ListEdgeNodesHandler(IEdgeReadStore store) : IQueryHandler<ListEdgeNodesQuery, IReadOnlyList<EdgeNodeDto>>
{
    public Task<IReadOnlyList<EdgeNodeDto>> HandleAsync(ListEdgeNodesQuery query, CancellationToken cancellationToken) => store.ListNodesAsync(query.SiteId, cancellationToken);
}

internal sealed class GetEdgeNodeHandler(IEdgeReadStore store) : IQueryHandler<GetEdgeNodeQuery, EdgeNodeDetailDto>
{
    public async Task<EdgeNodeDetailDto> HandleAsync(GetEdgeNodeQuery query, CancellationToken cancellationToken) =>
        await store.GetNodeAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Edge node {query.Id} not found.");
}

internal sealed class GetEdgeConfigHandler(IEdgeReadStore store) : IQueryHandler<GetEdgeConfigQuery, EdgeConfigDto>
{
    public async Task<EdgeConfigDto> HandleAsync(GetEdgeConfigQuery query, CancellationToken cancellationToken) =>
        await store.GetConfigAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Edge node {query.Id} not found.");
}
