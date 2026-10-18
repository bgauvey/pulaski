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
