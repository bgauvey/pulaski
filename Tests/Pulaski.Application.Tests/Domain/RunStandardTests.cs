using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

public class RunStandardTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static RunStandard New(decimal rate = 1200m) =>
        RunStandard.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), rate, CalculationBasis.IdealRate, At);

    [Fact]
    public void A_new_standard_is_calculated()
    {
        var standard = New();

        Assert.Equal(RateSource.Calculated, standard.RateSource);
        Assert.Equal(1200m, standard.EffectiveRatePerHour);
    }

    [Fact]
    public void A_modified_rate_is_the_gold_standard()
    {
        var standard = New().Modify(950m, "planner-1", "Die 4 runs slow", At.AddDays(1));

        Assert.Equal(RateSource.Modified, standard.RateSource);
        Assert.Equal(950m, standard.EffectiveRatePerHour);
        Assert.Equal("planner-1", standard.ModifiedBy);
    }

    [Fact]
    public void Recalculation_updates_the_calculated_rate_but_never_overrides_a_modified_one()
    {
        var standard = New().Modify(950m, "planner-1", null, At).Recalculate(1010m, CalculationBasis.RunHistory, At.AddDays(7));

        Assert.Equal(1010m, standard.CalculatedRatePerHour);
        Assert.Equal(CalculationBasis.RunHistory, standard.CalculationBasis);
        Assert.Equal(950m, standard.EffectiveRatePerHour);
        Assert.Equal(RateSource.Modified, standard.RateSource);
    }

    [Fact]
    public void Clearing_the_modification_returns_to_the_calculated_rate()
    {
        var standard = New().Modify(950m, "planner-1", "note", At).ClearModification();

        Assert.Equal(RateSource.Calculated, standard.RateSource);
        Assert.Equal(1200m, standard.EffectiveRatePerHour);
        Assert.Null(standard.ModifiedAt);
        Assert.Null(standard.ModifiedNote);
    }

    [Fact]
    public void Rules_reject_bad_input()
    {
        Assert.Throws<DomainRuleException>(() => New(0m));
        Assert.Throws<DomainRuleException>(() => New().Modify(-1m, "planner-1", null, At));
        Assert.Throws<DomainRuleException>(() => New().Modify(10m, " ", null, At));
        Assert.Throws<DomainRuleException>(() => New().Recalculate(10m, "guess", At));
        Assert.Throws<DomainRuleException>(() => New().ClearModification());
    }
}
