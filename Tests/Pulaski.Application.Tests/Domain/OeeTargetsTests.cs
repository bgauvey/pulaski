using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.Tests.Domain;

/// <summary>OEE target periods (D-073): which is in force, and how setting and ending change a scope's periods.</summary>
public class OeeTargetsTests
{
    private static readonly Guid Site = Guid.CreateVersion7();
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly OeeTargetValues Sixty = new(null, null, null, 60m);
    private static readonly OeeTargetValues Seventy = new(85m, null, null, 70m);

    private static OeeTarget T(Guid? equipment, DateOnly from, DateOnly? to, OeeTargetValues values) => new(Guid.CreateVersion7(), Site, equipment, from, to, values);

    [Fact]
    public void Equipment_follows_its_own_target_else_the_sites_whole()
    {
        var targets = new[] { T(null, Day.AddDays(-10), null, Sixty), T(Line, Day, Day.AddDays(5), Seventy) };

        Assert.Equal(Sixty, OeeTargets.InForce(targets, Line, Day.AddDays(-1))!.Values);
        Assert.Equal(Seventy, OeeTargets.InForce(targets, Line, Day)!.Values);
        Assert.Equal(Sixty, OeeTargets.InForce(targets, Line, Day.AddDays(5))!.Values);     // its own ended: the site's again
        Assert.Null(OeeTargets.InForce(targets, Line, Day.AddDays(-11)));
    }

    [Fact]
    public void Setting_later_ends_the_latest_and_setting_on_its_first_day_changes_it()
    {
        var first = OeeTargets.Set([], Guid.CreateVersion7(), Site, Line, Day, Sixty);
        Assert.Null(first.Changed);
        var open = first.Added!;
        Assert.Equal((Day, (DateOnly?)null), (open.EffectiveFrom, open.EffectiveTo));

        var later = OeeTargets.Set([open], Guid.CreateVersion7(), Site, Line, Day.AddDays(7), Seventy);
        Assert.Equal(Day.AddDays(7), later.Changed!.EffectiveTo);
        Assert.Equal((Day.AddDays(7), Seventy), (later.Added!.EffectiveFrom, later.Added.Values));

        var sameDay = OeeTargets.Set([open], Guid.CreateVersion7(), Site, Line, Day, Seventy);
        Assert.Equal((open.Id, Seventy), (sameDay.Changed!.Id, sameDay.Changed.Values));
        Assert.Null(sameDay.Added);

        // An ended period stays as it was when a new one starts after it.
        var ended = open with { EffectiveTo = Day.AddDays(3) };
        Assert.Null(OeeTargets.Set([ended], Guid.CreateVersion7(), Site, Line, Day.AddDays(7), Seventy).Changed);

        Assert.Throws<DomainRuleException>(() => OeeTargets.Set([open], Guid.CreateVersion7(), Site, Line, Day.AddDays(-1), Seventy));
        Assert.Throws<DomainRuleException>(() => OeeTargets.Set([], Guid.CreateVersion7(), Site, Line, Day, new(null, null, null, null)));
        Assert.Throws<DomainRuleException>(() => OeeTargets.Set([], Guid.CreateVersion7(), Site, Line, Day, new(100.5m, null, null, null)));
    }

    [Fact]
    public void Ending_needs_a_day_after_the_start_and_an_open_target()
    {
        var open = T(Line, Day, null, Sixty);
        Assert.Equal(Day.AddDays(1), OeeTargets.End([open], Day.AddDays(1)).EffectiveTo);
        Assert.Throws<DomainRuleException>(() => OeeTargets.End([open], Day));
        Assert.Throws<DomainRuleException>(() => OeeTargets.End([open with { EffectiveTo = Day.AddDays(2) }], Day.AddDays(3)));
        Assert.Throws<DomainRuleException>(() => OeeTargets.End([], Day));
    }
}
