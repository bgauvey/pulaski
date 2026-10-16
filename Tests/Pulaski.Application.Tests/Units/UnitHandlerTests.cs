using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Domain;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Tests.Units;

/// <summary>Units and material conversions (D-026).</summary>
public class UnitHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Unit _kg;
    private readonly Unit _lb;
    private readonly Unit _m;
    private readonly Unit _ft;
    private readonly Guid _profile = Guid.CreateVersion7();

    public UnitHandlerTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _lb = _f.Stores.Units.AddUnit("lb", "mass", 0.45359237m);
        _m = _f.Stores.Units.AddUnit("m", "length", 1m);
        _ft = _f.Stores.Units.AddUnit("ft", "length", 0.3048m);
        _f.Stores.Units.AddMaterial(_profile);
    }

    private SetMaterialConversionHandler Set() => new(_f.Stores.UnitOfWork, _f.Stores.Units, _f.Stores.Events, _f.Events);

    private RemoveMaterialConversionHandler Remove() => new(_f.Stores.UnitOfWork, _f.Stores.Units, _f.Stores.Events, _f.Events);

    private ConvertQuantityHandler Convert() => new(_f.Stores.Units);

    [Fact]
    public async Task A_unit_needs_a_factor_unless_it_is_packaging()
    {
        var create = new CreateUnitHandler(_f.Stores.UnitOfWork, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids);

        var box = await create.HandleAsync(new CreateUnitCommand("box", "Box", UnitDimensions.Other, null), CancellationToken.None);
        Assert.Null(_f.Stores.Units.Find(box.Id)!.Value.ToBaseFactor);

        await Assert.ThrowsAsync<ValidationException>(() => create.HandleAsync(new CreateUnitCommand("t", "Tonne", "mass", null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => create.HandleAsync(new CreateUnitCommand("bag", "Bag", UnitDimensions.Other, 25m), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() => create.HandleAsync(new CreateUnitCommand("box", "Box", UnitDimensions.Other, null), CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_name_and_active_flag_of_a_unit_change()
    {
        var update = new UpdateUnitHandler(_f.Stores.UnitOfWork, _f.Stores.Units, _f.Stores.Events, _f.Events);
        var current = _f.Stores.Units.Find(_lb.Id)!;

        await update.HandleAsync(new UpdateUnitCommand(_lb.Id, current.Version, "Pound (avoirdupois)", false), CancellationToken.None);

        var after = _f.Stores.Units.Find(_lb.Id)!.Value;
        Assert.Equal(("Pound (avoirdupois)", false, 0.45359237m), (after.Name, after.IsActive, after.ToBaseFactor));
        await Assert.ThrowsAsync<ConflictException>(() =>
            update.HandleAsync(new UpdateUnitCommand(_lb.Id, current.Version, "Pound", true), CancellationToken.None));
    }

    [Fact]
    public async Task A_material_conversion_converts_across_dimensions_both_ways()
    {
        await Set().HandleAsync(new SetMaterialConversionCommand(_profile, _m.Id, _kg.Id, 1.6m), CancellationToken.None);

        var feet = await Convert().HandleAsync(new ConvertQuantityQuery(10m, _ft.Id, _lb.Id, _profile), CancellationToken.None);
        Assert.Equal((true, 10.751504m), (feet.Converts, feet.Quantity!.Value));

        var back = await Convert().HandleAsync(new ConvertQuantityQuery(1600m, _kg.Id, _m.Id, _profile), CancellationToken.None);
        Assert.Equal(1000m, back.Quantity);

        var noMaterial = await Convert().HandleAsync(new ConvertQuantityQuery(1m, _m.Id, _kg.Id, null), CancellationToken.None);
        Assert.False(noMaterial.Converts);
        Assert.Equal(EventTypes.MaterialConversionSet, Assert.Single(_f.Stores.Events.Appended).Type);
    }

    [Fact]
    public async Task Setting_the_reverse_pair_replaces_it_and_a_second_route_is_refused()
    {
        await Set().HandleAsync(new SetMaterialConversionCommand(_profile, _m.Id, _kg.Id, 1.6m), CancellationToken.None);
        await Set().HandleAsync(new SetMaterialConversionCommand(_profile, _kg.Id, _m.Id, 0.5m), CancellationToken.None);

        var conversion = Assert.Single(await _f.Stores.Units.ListConversionsAsync(_profile, CancellationToken.None));
        Assert.Equal((_kg.Id, _m.Id, 0.5m), (conversion.FromUomId, conversion.ToUomId, conversion.Factor));

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Set().HandleAsync(new SetMaterialConversionCommand(_profile, _ft.Id, _lb.Id, 1m), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Set().HandleAsync(new SetMaterialConversionCommand(Guid.CreateVersion7(), _m.Id, _kg.Id, 1m), CancellationToken.None));
    }

    [Fact]
    public async Task A_conversion_is_removed_given_either_direction()
    {
        await Set().HandleAsync(new SetMaterialConversionCommand(_profile, _m.Id, _kg.Id, 1.6m), CancellationToken.None);

        await Remove().HandleAsync(new RemoveMaterialConversionCommand(_profile, _kg.Id, _m.Id), CancellationToken.None);

        Assert.Empty(await _f.Stores.Units.ListConversionsAsync(_profile, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Remove().HandleAsync(new RemoveMaterialConversionCommand(_profile, _m.Id, _kg.Id), CancellationToken.None));
    }
}
