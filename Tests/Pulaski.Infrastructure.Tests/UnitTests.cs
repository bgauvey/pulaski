using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Domain;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Units and material conversions end to end (D-026): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class UnitTests(DatabaseFixture db)
{
    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task Units_and_material_conversions_round_trip_and_convert()
    {
        var suffix = Guid.NewGuid().ToString("N")[..5];
        CreatedResponse kg = await db.SendAsync(new CreateUnitCommand("kg" + suffix, "Kilogram", "mass", 1m));
        CreatedResponse lb = await db.SendAsync(new CreateUnitCommand("lb" + suffix, "Pound", "mass", 0.45359237m));
        CreatedResponse box = await db.SendAsync(new CreateUnitCommand("box" + suffix, "Box", "other", null));

        var units = await QueryAsync<ListUomsQuery, IReadOnlyList<UomDto>>(new ListUomsQuery());
        var pound = Assert.Single(units, u => u.Id == lb.Id);
        Assert.Equal((0.45359237m, true), (pound.ToBaseFactor, pound.IsActive));
        Assert.NotNull(pound.BaseCode);
        Assert.Null(Assert.Single(units, u => u.Id == box.Id).ToBaseFactor);

        // The fixture's material is in metres: 1 m weighs 1.6 kg, and 250 m fill a box.
        await db.SendAsync(new SetMaterialConversionCommand(db.MaterialId, db.UomId, kg.Id, 1.6m));
        await db.SendAsync(new SetMaterialConversionCommand(db.MaterialId, box.Id, db.UomId, 250m));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new SetMaterialConversionCommand(db.MaterialId, box.Id, lb.Id, 900m)));

        var list = await QueryAsync<ListMaterialConversionsQuery, IReadOnlyList<MaterialConversionDto>>(new ListMaterialConversionsQuery(db.MaterialId));
        Assert.Contains(list, c => c.FromUomId == db.UomId && c.ToUomId == kg.Id && c.Factor == 1.6m);

        var boxInPounds = await QueryAsync<ConvertQuantityQuery, UnitConversionResultDto>(new ConvertQuantityQuery(1m, box.Id, lb.Id, db.MaterialId));
        Assert.Equal(881.849049m, boxInPounds.Quantity);

        await db.SendAsync(new RemoveMaterialConversionCommand(db.MaterialId, kg.Id, db.UomId));
        await db.SendAsync(new RemoveMaterialConversionCommand(db.MaterialId, box.Id, db.UomId));
        Assert.Empty(await QueryAsync<ListMaterialConversionsQuery, IReadOnlyList<MaterialConversionDto>>(new ListMaterialConversionsQuery(db.MaterialId)));
        Assert.False((await QueryAsync<ConvertQuantityQuery, UnitConversionResultDto>(new ConvertQuantityQuery(1m, box.Id, lb.Id, db.MaterialId))).Converts);
    }
}
