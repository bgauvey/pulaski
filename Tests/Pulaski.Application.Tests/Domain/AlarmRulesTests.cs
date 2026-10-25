using Pulaski.Domain.MachineData;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Alarms and warnings from code-valued tags (#26).</summary>
public class AlarmRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_number_raises_another_replaces_it_and_zero_clears_it()
    {
        Assert.Null(AlarmRules.Cleared(null, 12, true, T0));
        Assert.Equal(12, AlarmRules.Raised(null, 12, true));

        var open = new OpenAlarm(Guid.CreateVersion7(), T0, 12);
        Assert.Null(AlarmRules.Cleared(open, 12, true, T0.AddSeconds(5)));
        Assert.Null(AlarmRules.Raised(open, 12, true));

        Assert.Equal((open, T0.AddSeconds(9)), AlarmRules.Cleared(open, 7, true, T0.AddSeconds(9)));
        Assert.Equal(7, AlarmRules.Raised(open, 7, true));

        Assert.Equal((open, T0.AddMinutes(1)), AlarmRules.Cleared(open, 0, true, T0.AddMinutes(1)));
        Assert.Null(AlarmRules.Raised(open, 0, true));
        Assert.Null(AlarmRules.Raised(null, 0, true));
    }

    [Fact]
    public void Losing_the_signal_neither_raises_nor_clears_and_a_clear_never_comes_before_the_raise()
    {
        var open = new OpenAlarm(Guid.CreateVersion7(), T0, 12);
        Assert.Null(AlarmRules.Cleared(open, null, false, T0.AddSeconds(5)));
        Assert.Null(AlarmRules.Cleared(open, 0, false, T0.AddSeconds(5)));
        Assert.Null(AlarmRules.Raised(null, 4, false));

        Assert.Equal(T0, AlarmRules.Cleared(open, 0, true, T0.AddSeconds(-3))?.ClearedAt);
        Assert.Equal((int?)3, AlarmRules.Number(3.9m));
        Assert.Null(AlarmRules.Number(null));
        Assert.Null(AlarmRules.Number(1e12m));
    }
}
