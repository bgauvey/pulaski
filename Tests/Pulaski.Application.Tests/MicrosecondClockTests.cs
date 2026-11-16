using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Tests;

/// <summary>The application's clock keeps time as PostgreSQL does: to whole microseconds.</summary>
public class MicrosecondClockTests
{
    [Fact]
    public void Ticks_below_a_microsecond_are_dropped_and_the_offset_kept()
    {
        var at = new DateTimeOffset(2026, 11, 16, 0, 50, 50, TimeSpan.FromHours(-5)).AddTicks(7_012_889);

        var rounded = MicrosecondClock.ToMicroseconds(at);

        Assert.Equal(at.AddTicks(-9), rounded);
        Assert.Equal(TimeSpan.FromHours(-5), rounded.Offset);
        Assert.Equal(rounded, MicrosecondClock.ToMicroseconds(rounded));
    }

    [Fact]
    public void Now_is_whole_microseconds()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(0, MicrosecondClock.Instance.GetUtcNow().Ticks % 10);
        }
    }

    [Fact]
    public void The_application_uses_it_unless_a_clock_is_given()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddApplication();

        Assert.Contains(services, d => d.ServiceType == typeof(TimeProvider) && d.ImplementationInstance == MicrosecondClock.Instance);
    }
}
