using Pulaski.Contracts.Audit;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

/// <summary>The audit trail names who made a change and searches whole days (#129).</summary>
public class AuditViewTests
{
    private static AuditEventDto Event(string actor, string? name) =>
        new() { Id = Guid.Empty, Type = "t", Action = "a", SubjectType = "lot", Subject = Guid.Empty, ActorType = "user", ActorId = actor, ActorName = name, DataJson = "{}" };

    [Fact]
    public void Who_is_the_name_with_the_user_name_and_a_window_spans_whole_days()
    {
        Assert.Equal("Pat Lee (pat)", AuditView.Who(Event("pat", "Pat Lee")));
        Assert.Equal("terminal:ST-1", AuditView.Who(Event("terminal:ST-1", null)));
        var day = new DateOnly(2026, 11, 26);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero), AuditView.Start(day));
        Assert.True(AuditView.End(day) < new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        Assert.True(AuditView.End(day) > new DateTimeOffset(2026, 11, 26, 23, 59, 59, TimeSpan.Zero));
    }
}
