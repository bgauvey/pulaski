using Pulaski.Application.Abstractions;
using Pulaski.Contracts.ReferenceData;

namespace Pulaski.Application.ReferenceData;

public interface IReferenceDataReadStore
{
    Task<IReadOnlyList<UomDto>> ListUomsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MaterialDto>> ListMaterialsAsync(bool includeInactive, CancellationToken cancellationToken);
}

/// <summary>Write-side lookups used by commands, inside the current unit of work.</summary>
public interface IMaterialRepository
{
    /// <summary>True when the material exists and is active; locks it against deactivation until commit.</summary>
    Task<bool> IsActiveAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record ListUomsQuery : IQuery<IReadOnlyList<UomDto>>;

public sealed record ListMaterialsQuery(bool IncludeInactive) : IQuery<IReadOnlyList<MaterialDto>>;

internal sealed class ListUomsHandler(IReferenceDataReadStore store)
    : IQueryHandler<ListUomsQuery, IReadOnlyList<UomDto>>
{
    public Task<IReadOnlyList<UomDto>> HandleAsync(ListUomsQuery query, CancellationToken cancellationToken) =>
        store.ListUomsAsync(cancellationToken);
}

internal sealed class ListMaterialsHandler(IReferenceDataReadStore store)
    : IQueryHandler<ListMaterialsQuery, IReadOnlyList<MaterialDto>>
{
    public Task<IReadOnlyList<MaterialDto>> HandleAsync(ListMaterialsQuery query, CancellationToken cancellationToken) =>
        store.ListMaterialsAsync(query.IncludeInactive, cancellationToken);
}
