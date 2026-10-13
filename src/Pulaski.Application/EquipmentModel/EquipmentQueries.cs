using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Domain.EquipmentModel;

namespace Pulaski.Application.EquipmentModel;

public sealed record ListEquipmentQuery(Guid? SiteId) : IQuery<IReadOnlyList<EquipmentDto>>;

public sealed record GetEquipmentQuery(Guid Id) : IQuery<EquipmentDto>;

public sealed record ListEquipmentTypesQuery : IQuery<IReadOnlyList<EquipmentTypeDto>>;

internal sealed class ListEquipmentHandler(IEquipmentReadStore store)
    : IQueryHandler<ListEquipmentQuery, IReadOnlyList<EquipmentDto>>
{
    public Task<IReadOnlyList<EquipmentDto>> HandleAsync(ListEquipmentQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.SiteId, cancellationToken);
}

internal sealed class GetEquipmentHandler(IEquipmentReadStore store)
    : IQueryHandler<GetEquipmentQuery, EquipmentDto>
{
    public async Task<EquipmentDto> HandleAsync(GetEquipmentQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken)
        ?? throw new NotFoundException($"Equipment {query.Id} not found.");
}

internal sealed class ListEquipmentTypesHandler
    : IQueryHandler<ListEquipmentTypesQuery, IReadOnlyList<EquipmentTypeDto>>
{
    private static readonly IReadOnlyList<EquipmentTypeDto> Types =
        [.. EquipmentTypes.LevelRoles.Select(t => new EquipmentTypeDto(t.Level, t.Role))];

    public Task<IReadOnlyList<EquipmentTypeDto>> HandleAsync(ListEquipmentTypesQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Types);
}
