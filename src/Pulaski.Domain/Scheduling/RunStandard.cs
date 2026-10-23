namespace Pulaski.Domain.Scheduling;

/// <summary>
/// The planning rate for one material on one line (D-016). Pulaski maintains the calculated
/// rate; a person may set a modified rate, which is the gold standard until cleared.
/// Recalculating never touches the modified rate, so drift between the two stays visible.
/// </summary>
public sealed record RunStandard(
    Guid Id,
    Guid EquipmentId,
    Guid MaterialId,
    Guid UomId,
    decimal CalculatedRatePerHour,
    string CalculationBasis,
    DateTimeOffset CalculatedAt,
    decimal? ModifiedRatePerHour,
    DateTimeOffset? ModifiedAt,
    string? ModifiedBy,
    string? ModifiedNote)
{
    public string RateSource => ModifiedRatePerHour is null ? Scheduling.RateSource.Calculated : Scheduling.RateSource.Modified;

    /// <summary>The rate in force: the modified rate when set, otherwise the calculated one.</summary>
    public decimal EffectiveRatePerHour => ModifiedRatePerHour ?? CalculatedRatePerHour;

    /// <summary>A new standard, seeded from a calculation (usually the capability's ideal rate).</summary>
    /// <exception cref="DomainRuleException">The rate or basis is invalid.</exception>
    public static RunStandard Create(
        Guid id, Guid equipmentId, Guid materialId, Guid uomId, decimal ratePerHour, string basis, DateTimeOffset at)
    {
        EnsurePositive(ratePerHour);
        EnsureBasis(basis);
        return new RunStandard(id, equipmentId, materialId, uomId, ratePerHour, basis, at, null, null, null, null);
    }

    /// <summary>Records a new calculated rate. A modified rate, if any, stays in force.</summary>
    /// <exception cref="DomainRuleException">The rate or basis is invalid.</exception>
    public RunStandard Recalculate(decimal ratePerHour, string basis, DateTimeOffset at)
    {
        EnsurePositive(ratePerHour);
        EnsureBasis(basis);
        return this with { CalculatedRatePerHour = ratePerHour, CalculationBasis = basis, CalculatedAt = at };
    }

    /// <summary>
    /// Follows a capability's new ideal rate (D-034): while the calculated rate is still based on the
    /// ideal rate it takes the new one, in its unit; a rate calculated from run history is left alone.
    /// A modified rate stays in force either way.
    /// </summary>
    /// <exception cref="DomainRuleException">The unit would change under a modified rate, which is in the old unit.</exception>
    public RunStandard FollowIdealRate(decimal ratePerHour, Guid uomId, DateTimeOffset at)
    {
        if (CalculationBasis != Scheduling.CalculationBasis.IdealRate)
        {
            return this;
        }

        if (uomId != UomId && ModifiedRatePerHour is not null)
        {
            throw new DomainRuleException("The planning rate has a modified rate in the old unit; clear it before changing the rate unit.");
        }

        return ratePerHour == CalculatedRatePerHour && uomId == UomId
            ? this
            : Recalculate(ratePerHour, Scheduling.CalculationBasis.IdealRate, at) with { UomId = uomId };
    }

    /// <summary>
    /// Goes back to the capability's ideal rate (D-045), for a standard calculated from run history, so it follows the ideal
    /// rate again. A modified rate stays in force.
    /// </summary>
    /// <exception cref="DomainRuleException">The unit would change under a modified rate, which is in the old unit.</exception>
    public RunStandard UseIdealRate(decimal ratePerHour, Guid uomId, DateTimeOffset at)
    {
        if (uomId != UomId && ModifiedRatePerHour is not null)
        {
            throw new DomainRuleException("The planning rate has a modified rate in the old unit; clear it before changing the rate unit.");
        }

        return Recalculate(ratePerHour, Scheduling.CalculationBasis.IdealRate, at) with { UomId = uomId };
    }

    /// <summary>Sets the gold-standard rate.</summary>
    /// <exception cref="DomainRuleException">The rate is invalid.</exception>
    public RunStandard Modify(decimal ratePerHour, string by, string? note, DateTimeOffset at)
    {
        EnsurePositive(ratePerHour);
        if (string.IsNullOrWhiteSpace(by))
        {
            throw new DomainRuleException("A modified rate must record who set it.");
        }

        return this with { ModifiedRatePerHour = ratePerHour, ModifiedAt = at, ModifiedBy = by, ModifiedNote = note };
    }

    /// <summary>Drops the modified rate, so the calculated rate is in force again.</summary>
    /// <exception cref="DomainRuleException">There is no modified rate.</exception>
    public RunStandard ClearModification()
    {
        if (ModifiedRatePerHour is null)
        {
            throw new DomainRuleException("This run standard has no modified rate to clear.");
        }

        return this with { ModifiedRatePerHour = null, ModifiedAt = null, ModifiedBy = null, ModifiedNote = null };
    }

    private static void EnsurePositive(decimal ratePerHour)
    {
        if (ratePerHour <= 0)
        {
            throw new DomainRuleException("Rate must be greater than zero.");
        }
    }

    private static void EnsureBasis(string basis)
    {
        if (!Scheduling.CalculationBasis.All.Contains(basis))
        {
            throw new DomainRuleException($"Unknown calculation basis '{basis}'.");
        }
    }
}
