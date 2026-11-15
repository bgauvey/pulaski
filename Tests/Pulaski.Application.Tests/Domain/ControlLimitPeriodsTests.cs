using Pulaski.Domain;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Periods of control limits (D-099): one set at a time per scope, the line's own first.</summary>
public class ControlLimitPeriodsTests
{
    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Characteristic = Guid.CreateVersion7();
    private static readonly Guid Material = Guid.CreateVersion7();
    private static readonly Guid Line = Guid.CreateVersion7();

    private static ControlLimit Limit(DateTimeOffset from, DateTimeOffset? to = null, Guid? line = null, string method = LimitMethods.Computed, string? note = null) =>
        new(Guid.CreateVersion7(), Characteristic, Material, line, ChartTypes.IMr, 1, new LimitSet(10, 13, 7, 1.1, 3.6, 0), method,
            method == LimitMethods.Computed ? new LimitBaseline(from.AddDays(-30), from, 25, []) : null, note, from, to, "quality", from);

    [Fact]
    public void New_limits_end_the_open_period_before_them_and_refuse_any_other_overlap()
    {
        var open = Limit(Jan);
        var ended = ControlLimitPeriods.Place(Limit(Jan.AddDays(10)), [open]);
        Assert.Equal(Jan.AddDays(10), Assert.Single(ended).EffectiveTo);

        Assert.Throws<DomainRuleException>(() => ControlLimitPeriods.Place(Limit(Jan.AddDays(-5)), [open]));
        Assert.Throws<DomainRuleException>(() => ControlLimitPeriods.Place(Limit(Jan.AddDays(5)), [Limit(Jan, Jan.AddDays(20))]));
        Assert.Empty(ControlLimitPeriods.Place(Limit(Jan.AddDays(5), line: Line), [open]));
    }

    [Fact]
    public void The_line_uses_its_own_limits_first_then_the_materials()
    {
        var material = Limit(Jan);
        var own = Limit(Jan.AddDays(5), Jan.AddDays(10), Line);

        Assert.Equal(own, ControlLimitPeriods.InForce([material, own], Line, Jan.AddDays(6)));
        Assert.Equal(material, ControlLimitPeriods.InForce([material, own], Line, Jan.AddDays(12)));
        Assert.Equal(material, ControlLimitPeriods.InForce([material, own], null, Jan.AddDays(6)));
        Assert.Null(ControlLimitPeriods.InForce([own], null, Jan.AddDays(6)));
    }

    [Fact]
    public void Limits_are_in_order_and_manual_ones_say_why()
    {
        Assert.Throws<DomainRuleException>(() => ControlLimitPeriods.Ensure(Limit(Jan) with { Limits = new LimitSet(10, 9, 7, 1, 3, 0) }));
        Assert.Throws<DomainRuleException>(() => ControlLimitPeriods.Ensure(Limit(Jan) with { Limits = new LimitSet(10, 13, 7) }));
        Assert.Throws<DomainRuleException>(() => ControlLimitPeriods.Ensure(Limit(Jan, method: LimitMethods.Manual)));
        ControlLimitPeriods.Ensure(Limit(Jan, method: LimitMethods.Manual, note: "Customer limits"));
        Assert.Throws<DomainRuleException>(() => ControlLimitPeriods.Ensure(Limit(Jan) with { ChartType = ChartTypes.P, Limits = new LimitSet(0.05, 0.1, -0.01) }));

        Assert.Throws<DomainRuleException>(() => Limit(Jan).End(Jan));
        Assert.Equal(Jan.AddDays(1), Limit(Jan).End(Jan.AddDays(1)).EffectiveTo);
        Assert.True(ControlLimitPeriods.Same(new LimitSet(3.08, 3.1954, 2.9646), new LimitSet(3.08 + 1e-12, 3.1954, 2.9646)));
        Assert.False(ControlLimitPeriods.Same(new LimitSet(3.08, 3.1954, 2.9646), new LimitSet(3.081, 3.1954, 2.9646)));
    }
}
