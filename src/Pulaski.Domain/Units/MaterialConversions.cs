namespace Pulaski.Domain.Units;

/// <summary>Rules for a material's own unit conversions (D-026).</summary>
public static class MaterialConversions
{
    /// <summary>
    /// Checks <paramref name="conversion"/> can be added to a material that already has
    /// <paramref name="others"/> (not including this pair, in either direction). It must link two
    /// different units that don't already convert for the material, so there is only ever one
    /// answer: two standard units of one dimension already convert by their factors, and a second
    /// route through other conversions could disagree with the first.
    /// </summary>
    /// <exception cref="DomainRuleException">The conversion is not allowed.</exception>
    public static void EnsureCanAdd(IReadOnlyCollection<Unit> units, IReadOnlyCollection<MaterialConversion> others, MaterialConversion conversion)
    {
        var from = units.FirstOrDefault(u => u.Id == conversion.FromUomId)
            ?? throw new DomainRuleException($"Unit {conversion.FromUomId} not found.");
        var to = units.FirstOrDefault(u => u.Id == conversion.ToUomId)
            ?? throw new DomainRuleException($"Unit {conversion.ToUomId} not found.");

        if (from.Id == to.Id)
        {
            throw new DomainRuleException("A conversion needs two different units.");
        }

        if (conversion.Factor <= 0)
        {
            throw new DomainRuleException("The factor must be greater than zero.");
        }

        if (!from.IsPackaging && !to.IsPackaging && from.Dimension == to.Dimension)
        {
            throw new DomainRuleException($"{from.Code} and {to.Code} already convert by their standard factors, for every material.");
        }

        if (new UnitConverter(units, others).Factor(from.Id, to.Id) is { } existing)
        {
            throw new DomainRuleException(
                $"For this material {from.Code} already converts to {to.Code} (1 {from.Code} = {existing:0.######} {to.Code}) through its other conversions; change one of those instead.");
        }
    }
}
