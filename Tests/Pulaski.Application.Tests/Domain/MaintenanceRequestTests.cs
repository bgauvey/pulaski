using Pulaski.Domain;
using Pulaski.Domain.Maintenance;

namespace Pulaski.Application.Tests.Domain;

/// <summary>A maintenance request is raised, started and closed with what was done (#127, D-115).</summary>
public class MaintenanceRequestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private static MaintenanceRequest Raised(string? priority = null) =>
        MaintenanceRequest.Raise(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), MaintenanceRequest.NumberFor(42), priority, " Belt slipping ", T0, "lee");

    [Fact]
    public void A_request_needs_a_description_and_a_known_priority()
    {
        var r = Raised();
        Assert.Equal(("MR-000042", "open", "normal", "Belt slipping"), (r.Number, r.Status, r.Priority, r.Description));
        Assert.Equal("high", Raised("high").Priority);
        Assert.Throws<DomainRuleException>(() => Raised("urgent"));
        Assert.Throws<DomainRuleException>(() => MaintenanceRequest.Raise(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "MR-1", null, " ", T0, "lee"));
        Assert.Throws<DomainRuleException>(() => MaintenanceRequest.Raise(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "MR-1", null, new string('x', 1001), T0, "lee"));
    }

    [Fact]
    public void It_is_started_once_and_closed_with_what_was_done()
    {
        var started = Raised().Start(T0.AddMinutes(20), "ann");
        Assert.Equal(("in_progress", "ann"), (started.Status, started.StartedBy));
        Assert.Throws<DomainRuleException>(() => started.Start(T0.AddMinutes(25), "ann"));
        Assert.Throws<DomainRuleException>(() => started.Close(T0.AddHours(1), "ann", " "));

        var closed = started.Close(T0.AddHours(1), "ann", "Belt replaced");
        Assert.Equal(("closed", "Belt replaced", T0.AddMinutes(20)), (closed.Status, closed.Resolution, closed.StartedAt!.Value));
        Assert.Throws<DomainRuleException>(() => closed.Close(T0.AddHours(2), "ann", "Again"));
        Assert.Throws<DomainRuleException>(() => closed.Start(T0.AddHours(2), "ann"));
        Assert.Equal((TimeSpan.FromMinutes(20), TimeSpan.FromHours(1)), (MaintenanceRules.Waited(closed, T0.AddDays(1)), MaintenanceRules.Age(closed, T0.AddDays(1))));
    }

    [Fact]
    public void Closing_one_nobody_started_starts_it_at_the_same_moment()
    {
        var r = Raised();
        Assert.Equal(TimeSpan.FromMinutes(30), MaintenanceRules.Waited(r, T0.AddMinutes(30)));
        var closed = r.Close(T0.AddMinutes(45), "ann", "Not needed: it was the guard switch");
        Assert.Equal((T0.AddMinutes(45), "ann"), (closed.StartedAt!.Value, closed.StartedBy));
    }
}
