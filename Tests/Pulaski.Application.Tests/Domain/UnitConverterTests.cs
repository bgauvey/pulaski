using Pulaski.Domain;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Unit conversions (D-026): standard factors within a dimension, a material's own conversions across them.</summary>
public class UnitConverterTests
{
    private static readonly Unit Kg = new(Guid.CreateVersion7(), "kg", "mass", 1m);
    private static readonly Unit Lb = new(Guid.CreateVersion7(), "lb", "mass", 0.45359237m);
    private static readonly Unit M = new(Guid.CreateVersion7(), "m", "length", 1m);
    private static readonly Unit Ft = new(Guid.CreateVersion7(), "ft", "length", 0.3048m);
    private static readonly Unit In = new(Guid.CreateVersion7(), "in", "length", 0.0254m);
    private static readonly Unit Ea = new(Guid.CreateVersion7(), "ea", "count", 1m);
    private static readonly Unit Dz = new(Guid.CreateVersion7(), "dz", "count", 12m);
    private static readonly Unit Box = new(Guid.CreateVersion7(), "box", "other", null);
    private static readonly Unit Pallet = new(Guid.CreateVersion7(), "pal", "other", null);

    private static readonly Unit[] Units = [Kg, Lb, M, Ft, In, Ea, Dz, Box, Pallet];

    [Fact]
    public void Standard_units_convert_within_their_dimension_for_any_material()
    {
        var converter = new UnitConverter(Units, []);

        Assert.Equal(45.359237m, converter.Convert(100m, Lb.Id, Kg.Id));
        Assert.Equal(12m, converter.Convert(1m, Ft.Id, In.Id));
        Assert.Equal(1m, converter.Convert(0.3048m, M.Id, Ft.Id));
        Assert.Equal(36m, converter.Convert(3m, Dz.Id, Ea.Id));
        Assert.Null(converter.Factor(M.Id, Kg.Id));
        Assert.Null(converter.Factor(Box.Id, Ea.Id));
        Assert.Equal(1m, converter.Factor(Box.Id, Box.Id));
    }

    [Fact]
    public void A_material_conversion_links_dimensions_and_combines_with_the_standard_factors()
    {
        // A profile weighing 1.6 kg/m, packed 600 m to a pallet.
        var converter = new UnitConverter(Units, [new(M.Id, Kg.Id, 1.6m), new(Pallet.Id, M.Id, 600m)]);

        Assert.Equal(1600m, converter.Convert(1000m, M.Id, Kg.Id));
        Assert.Equal(1000m, converter.Convert(1600m, Kg.Id, M.Id));
        Assert.Equal(1.07515m, converter.Convert(1m, Ft.Id, Lb.Id));
        Assert.Equal(960m, converter.Convert(1m, Pallet.Id, Kg.Id));
        Assert.Equal(0.5m, converter.Convert(300m, M.Id, Pallet.Id));
    }

    [Fact]
    public void Packaging_units_convert_only_through_a_material_conversion()
    {
        var converter = new UnitConverter(Units, [new(Box.Id, Ea.Id, 500m)]);

        Assert.Equal(1500m, converter.Convert(3m, Box.Id, Ea.Id));
        Assert.Equal(125m, converter.Convert(3m, Box.Id, Dz.Id));
        Assert.Null(converter.Factor(Box.Id, Kg.Id));
        var refused = Assert.Throws<DomainRuleException>(() => converter.Convert(1m, Box.Id, Kg.Id));
        Assert.Contains("box doesn't convert to kg", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_conversion_must_link_two_units_that_do_not_already_convert()
    {
        MaterialConversions.EnsureCanAdd(Units, [], new(M.Id, Kg.Id, 1.6m));

        Assert.Throws<DomainRuleException>(() => MaterialConversions.EnsureCanAdd(Units, [], new(M.Id, M.Id, 1m)));
        Assert.Throws<DomainRuleException>(() => MaterialConversions.EnsureCanAdd(Units, [], new(M.Id, Kg.Id, 0m)));

        var standard = Assert.Throws<DomainRuleException>(() => MaterialConversions.EnsureCanAdd(Units, [], new(Kg.Id, Lb.Id, 2.2m)));
        Assert.Contains("standard factors", standard.Message, StringComparison.Ordinal);

        // With m to kg in place, ft to lb would be a second route that could disagree.
        var second = Assert.Throws<DomainRuleException>(() => MaterialConversions.EnsureCanAdd(Units, [new(M.Id, Kg.Id, 1.6m)], new(Ft.Id, Lb.Id, 1m)));
        Assert.Contains("already converts", second.Message, StringComparison.Ordinal);
    }
}
