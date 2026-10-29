using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Retiming, removing, undoing and trimming stops (#68).</summary>
public class StopAdjustmentRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private static StopInterval Stop(int from, int? to) => new(Guid.CreateVersion7(), T0.AddMinutes(from), to is { } t ? T0.AddMinutes(t) : null);

    [Fact]
    public void What_is_left_of_a_late_stop_is_the_time_nothing_covers()
    {
        static IEnumerable<(int, int)> Minutes(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> parts) =>
            parts.Select(p => ((int)(p.Start - T0).TotalMinutes, (int)(p.End - T0).TotalMinutes));

        Assert.Equal([(0, 10), (20, 30)], Minutes(StopAdjustments.Uncovered(T0, T0.AddMinutes(30), [Stop(10, 20)])));
        Assert.Equal([(15, 20)], Minutes(StopAdjustments.Uncovered(T0, T0.AddMinutes(30), [Stop(-5, 15), Stop(20, 40)])));
        Assert.Equal([(0, 5), (25, 30)], Minutes(StopAdjustments.Uncovered(T0, T0.AddMinutes(30), [Stop(5, 15), Stop(10, 25)])));   // covers overlapping each other
        Assert.Empty(StopAdjustments.Uncovered(T0, T0.AddMinutes(30), [Stop(-10, 40)]));
        Assert.Equal([(0, 20)], Minutes(StopAdjustments.Uncovered(T0, T0.AddMinutes(30), [Stop(20, null)])));                    // an open stop covers the rest
        Assert.Equal([(0, 30)], Minutes(StopAdjustments.Uncovered(T0, T0.AddMinutes(30), [Stop(40, 50)])));
    }

    [Fact]
    public void A_retime_keeps_to_the_past_and_clear_of_other_stops()
    {
        var stop = Stop(0, 10);
        var now = T0.AddHours(1);
        StopAdjustments.EnsureCanRetime(stop, false, T0.AddMinutes(1), T0.AddMinutes(12), now, [stop, Stop(15, 20)]);

        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanRetime(stop, false, T0, T0.AddMinutes(10), now, [stop]));
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanRetime(stop, false, T0, T0.AddMinutes(16), now, [stop, Stop(15, 20)]));
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanRetime(stop, false, T0, T0.AddHours(2), now, [stop]));
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanRetime(stop, true, T0, T0.AddMinutes(5), now, [stop]));
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanRetime(Stop(0, null), false, T0, T0.AddMinutes(5), now, []));
    }

    [Fact]
    public void Only_a_stop_entered_by_hand_is_removed_and_only_an_untouched_adjustment_is_undone()
    {
        var stop = Stop(0, 10);
        StopAdjustments.EnsureCanVoid(stop, StopSources.Manual, false);
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanVoid(stop, StopSources.Machine, false));
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanVoid(stop, StopSources.Manual, true));

        var (a, b) = (Stop(0, 5), Stop(5, 10));
        StopAdjustments.EnsureCanUndo(StopSources.Split, [(a, false), (b, false)], [stop], [a, b, Stop(20, 30)]);
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanUndo(StopSources.Split, [(a, false), (b, true)], [stop], [a]));
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanUndo(StopSources.Split, [(a, false), (b, false)], [stop], [a, b, Stop(8, 12)]));
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanUndo(StopSources.Manual, [(a, false)], [stop], [a]));
        Assert.Throws<DomainRuleException>(() => StopAdjustments.EnsureCanUndo(StopSources.Trim, [(a, false)], [stop], [a]));
    }
}
