using Microsoft.Extensions.Time.Testing;
using Pulaski.Application.Health;

namespace Pulaski.Application.Tests.Health;

/// <summary>What the health page calls healthy, worth a look, failing or off (#141).</summary>
public class SystemHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_service_fails_on_its_last_round_and_warns_after_three_rounds_without_one()
    {
        var every = TimeSpan.FromMinutes(1);
        ServiceRound Round(DateTimeOffset? ok, DateTimeOffset? failed) => new("x", true, every, Now.AddHours(-1), ok, failed, "boom");
        Assert.Equal("ok", HealthRules.Service(Round(Now.AddSeconds(-30), null), Now).Status);
        Assert.Equal("failing", HealthRules.Service(Round(Now.AddMinutes(-2), Now.AddSeconds(-5)), Now).Status);
        Assert.Equal("ok", HealthRules.Service(Round(Now.AddSeconds(-5), Now.AddMinutes(-2)), Now).Status);
        Assert.Equal("warning", HealthRules.Service(Round(Now.AddMinutes(-4), null), Now).Status);
        Assert.Equal("warning", HealthRules.Service(Round(null, null), Now).Status);
        Assert.Equal("off", HealthRules.Service(new ServiceRound("x", false, null, null, null, null, null), Now).Status);
        Assert.Equal("ok", HealthRules.Service(new ServiceRound("live", true, null, Now.AddDays(-1), null, null, null), Now).Status);
    }

    [Fact]
    public void An_edge_node_is_quiet_after_two_minutes_and_lost_after_an_hour()
    {
        Assert.Equal("ok", HealthRules.EdgeNode(true, Now.AddSeconds(-30), Now).Status);
        Assert.Equal("warning", HealthRules.EdgeNode(true, Now.AddMinutes(-10), Now).Status);
        Assert.Equal("failing", HealthRules.EdgeNode(true, Now.AddHours(-2), Now).Status);
        Assert.Equal("warning", HealthRules.EdgeNode(true, null, Now).Status);
        Assert.Equal("off", HealthRules.EdgeNode(false, null, Now).Status);
    }

    [Fact]
    public void A_feed_fails_on_a_failed_delivery_and_warns_when_its_queue_is_old()
    {
        Assert.Equal("failing", HealthRules.Feed(true, 0, null, 2, Now).Status);
        Assert.Equal("warning", HealthRules.Feed(true, 40, Now.AddMinutes(-20), 0, Now).Status);
        Assert.Equal("ok", HealthRules.Feed(true, 3, Now.AddMinutes(-1), 0, Now).Status);
        Assert.Equal("off", HealthRules.Feed(false, 0, null, 0, Now).Status);
        Assert.Equal("warning", HealthRules.Feed(false, 5, Now.AddDays(-1), 0, Now).Status);
    }

    [Fact]
    public void A_connection_fails_on_a_failed_run_and_warns_when_overdue_or_not_applied()
    {
        Assert.Equal("failing", HealthRules.Connection(true, true, "failed", Now.AddHours(1), Now).Status);
        Assert.Equal("warning", HealthRules.Connection(true, true, "not_applied", null, Now).Status);
        Assert.Equal("warning", HealthRules.Connection(true, true, "applied", Now.AddHours(-3), Now).Status);
        Assert.Equal("ok", HealthRules.Connection(true, false, "applied", Now.AddHours(-3), Now).Status);
        Assert.Equal("off", HealthRules.Connection(false, true, "failed", null, Now).Status);
    }

    [Fact]
    public void The_register_keeps_each_services_latest_success_and_failure()
    {
        var time = new FakeTimeProvider(Now);
        var health = new ServiceHealth(time);
        health.Started("a", TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromSeconds(10));
        health.Succeeded("a");
        health.Failed("b", new string('x', 400));
        health.Disabled("c");
        var rounds = health.Snapshot();
        Assert.Equal(["a", "b", "c"], rounds.Select(r => r.Key));
        Assert.Equal(Now.AddSeconds(10), rounds[0].SucceededAt);
        Assert.Equal(301, rounds[1].Error!.Length);
        Assert.False(rounds[2].Enabled);
        Assert.Equal("failing", HealthStatus.Worst(["ok", "off", "failing", "warning"]));
        Assert.Equal("ok", HealthStatus.Worst(["off"]));
    }
}
