using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Materials;

namespace Pulaski.Application.Materials;

public sealed record GetMaterialQuery(Guid Id) : IQuery<MaterialDetailDto>;

internal sealed class GetMaterialHandler(IMaterialReadStore store) : IQueryHandler<GetMaterialQuery, MaterialDetailDto>
{
    public async Task<MaterialDetailDto> HandleAsync(GetMaterialQuery query, CancellationToken cancellationToken) =>
        await store.GetDetailAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Material {query.Id} not found.");
}
