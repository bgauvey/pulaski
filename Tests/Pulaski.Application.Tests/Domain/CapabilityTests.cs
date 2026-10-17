using Pulaski.Domain;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Capabilities: what can run where, over a period (D-020, D-034).</summary>
public class CapabilityTests
{
    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid PerHour = Guid.CreateVersion7();

    private static CapabilityChange Change(decimal rate = 900m, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(rate, PerHour, TimeSpan.FromMinutes(45), 2m, false, from ?? Jan, to);

    private static Capability New(DateTimeOffset? to = null) =>
        Capability.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Change(to: to));

    [Fact]
    public void Values_must_be_in_range_and_the_period_must_end_after_it_starts()
    {
        Assert.Throws<DomainRuleException>(() => Capability.Create(Guid.CreateVersion7(), Guid.Empty, Guid.Empty, Change(rate: 0m)));
        Assert.Throws<DomainRuleException>(() => Capability.Create(Guid.CreateVersion7(), Guid.Empty, Guid.Empty, Change(to: Jan)));
        Assert.Throws<DomainRuleException>(() => New().Edit(Change() with { StandardChangeover = TimeSpan.FromDays(8) }));
        Assert.Throws<DomainRuleException>(() => New().Edit(Change() with { StandardChangeover = TimeSpan.FromMinutes(-1) }));
        Assert.Throws<DomainRuleException>(() => New().Edit(Change() with { StandardCrewSize = 0m }));
        Assert.Equal(1200m, New().Edit(Change(rate: 1200m)).IdealRatePerHour);
    }

    [Fact]
    public void Ending_stops_it_now_and_only_once()
    {
        var now = Jan.AddMonths(3);
        var ended = New().End(now);

        Assert.Equal(now, ended.EffectiveTo);
        Assert.False(ended.IsInEffect(now));
        Assert.True(ended.IsInEffect(now.AddTicks(-1)));
        Assert.Throws<DomainRuleException>(() => ended.End(now.AddDays(1)));
        Assert.Throws<DomainRuleException>(() => New().End(Jan));
        Assert.Equal(now, New(to: Jan.AddYears(1)).End(now).EffectiveTo);
    }

    [Theory]
    [InlineData(-30, 0, false)]
    [InlineData(-30, 1, true)]
    [InlineData(90, null, false)]
    [InlineData(89, null, true)]
    [InlineData(-30, null, true)]
    public void Periods_overlap_when_they_share_an_instant(int fromDays, int? toDays, bool overlaps)
    {
        var march = New(to: Jan.AddDays(90));

        Assert.Equal(overlaps, march.Overlaps(Jan.AddDays(fromDays), toDays is { } t ? Jan.AddDays(t) : null));
    }
}
