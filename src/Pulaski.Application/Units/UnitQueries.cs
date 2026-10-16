using Pulaski.Application.Abstractions;
using Pulaski.Contracts.ReferenceData;

namespace Pulaski.Application.Units;

public sealed record ListMaterialConversionsQuery(Guid MaterialId) : IQuery<IReadOnlyList<MaterialConversionDto>>;

/// <summary>Converts a quantity between units; with a material, its own conversions apply too (D-026).</summary>
public sealed record ConvertQuantityQuery(decimal Quantity, Guid FromUomId, Guid ToUomId, Guid? MaterialId) : IQuery<UnitConversionResultDto>;

internal sealed class ListMaterialConversionsHandler(IUnitReadStore store)
    : IQueryHandler<ListMaterialConversionsQuery, IReadOnlyList<MaterialConversionDto>>
{
    public Task<IReadOnlyList<MaterialConversionDto>> HandleAsync(ListMaterialConversionsQuery query, CancellationToken cancellationToken) =>
        store.ListConversionsAsync(query.MaterialId, cancellationToken);
}

internal sealed class ConvertQuantityHandler(IUnitReadStore store) : IQueryHandler<ConvertQuantityQuery, UnitConversionResultDto>
{
    public async Task<UnitConversionResultDto> HandleAsync(ConvertQuantityQuery query, CancellationToken cancellationToken)
    {
        var units = await store.GetUnitsAsync(cancellationToken);
        var converter = query.MaterialId is { } materialId
            ? UnitConversions.For(materialId, units, await store.GetConversionsAsync([materialId], cancellationToken))
            : UnitConversions.Standard(units);

        if (converter.Factor(query.FromUomId, query.ToUomId) is not { } factor)
        {
            return new UnitConversionResultDto
            {
                Converts = false,
                Message = query.MaterialId is null
                    ? $"{converter.Code(query.FromUomId)} doesn't convert to {converter.Code(query.ToUomId)} without a material; pick one."
                    : $"{converter.Code(query.FromUomId)} doesn't convert to {converter.Code(query.ToUomId)} for this material; add a conversion for it.",
            };
        }

        return new UnitConversionResultDto
        {
            Converts = true,
            Factor = Math.Round(factor, 12, MidpointRounding.AwayFromZero),
            Quantity = Math.Round(query.Quantity * factor, 6, MidpointRounding.AwayFromZero),
        };
    }
}
