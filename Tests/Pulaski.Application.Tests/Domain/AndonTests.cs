using Pulaski.Application.Andon;
using Pulaski.Contracts.Andon;
using Pulaski.Domain;
using Pulaski.Domain.Andon;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Andon call types and calls (#125, D-108).</summary>
public class AndonTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Site = Guid.CreateVersion7();

    private static AndonType Maint(bool active = true) => AndonType.Create(Guid.CreateVersion7(), Site, " maint ", " Maintenance ", 10, 1) with { IsActive = active };

    [Fact]
    public void A_type_keeps_an_upper_case_code_a_name_and_an_escalation_time()
    {
        var type = Maint();
        Assert.Equal(("MAINT", "Maintenance", 10, true), (type.Code, type.Name, type.EscalateAfterMinutes, type.IsActive));

        Assert.Throws<DomainRuleException>(() => AndonType.Create(Guid.CreateVersion7(), Site, "no spaces", "x", 10, 0));
        Assert.Throws<DomainRuleException>(() => AndonType.Create(Guid.CreateVersion7(), Site, "", "x", 10, 0));
        Assert.Throws<DomainRuleException>(() => type.Edit(" ", 10, 0, true));
        Assert.Throws<DomainRuleException>(() => type.Edit("x", 0, 0, true));
        Assert.Throws<DomainRuleException>(() => type.Edit("x", AndonType.MaxEscalateMinutes + 1, 0, true));
    }

    [Fact]
    public void A_call_is_raised_acknowledged_once_and_resolved_once()
    {
        var call = AndonCall.Raise(Guid.CreateVersion7(), Site, Guid.CreateVersion7(), Maint(), "  ", T0, "dana", "ST-1");
        Assert.Equal((AndonStatuses.Open, null, "ST-1"), (call.Status, call.Note, call.RaisedFrom));

        var acknowledged = call.Acknowledge(T0.AddMinutes(3), "lee");
        Assert.Equal((AndonStatuses.Acknowledged, "lee"), (acknowledged.Status, acknowledged.AcknowledgedBy));
        Assert.Contains("lee", Assert.Throws<DomainRuleException>(() => acknowledged.Acknowledge(T0.AddMinutes(4), "sam")).Message, StringComparison.Ordinal);

        var resolved = acknowledged.Resolve(T0.AddMinutes(20), "sam", " belt replaced ");
        Assert.Equal((AndonStatuses.Resolved, "lee", "sam", "belt replaced"), (resolved.Status, resolved.AcknowledgedBy, resolved.ResolvedBy, resolved.Resolution));
        Assert.Throws<DomainRuleException>(() => resolved.Resolve(T0.AddMinutes(21), "sam", null));
        Assert.Throws<DomainRuleException>(() => resolved.Acknowledge(T0.AddMinutes(21), "sam"));
    }

    [Fact]
    public void Resolving_an_unanswered_call_acknowledges_it_then_and_a_switched_off_type_takes_no_calls()
    {
        var resolved = AndonCall.Raise(Guid.CreateVersion7(), Site, Guid.CreateVersion7(), Maint(), null, T0, "dana", null).Resolve(T0.AddMinutes(5), "lee", null);
        Assert.Equal((T0.AddMinutes(5), "lee"), (resolved.AcknowledgedAt, resolved.AcknowledgedBy));

        Assert.Throws<DomainRuleException>(() => AndonCall.Raise(Guid.CreateVersion7(), Site, Guid.CreateVersion7(), Maint(active: false), null, T0, "dana", null));
        Assert.Throws<DomainRuleException>(() => AndonCall.Raise(
            Guid.CreateVersion7(), Site, Guid.CreateVersion7(), Maint(), new string('x', AndonCall.MaxText + 1), T0, "dana", null));
    }

    [Fact]
    public void A_call_escalates_while_unanswered_past_its_types_time_and_waits_until_acknowledged()
    {
        var type = Maint();
        var call = AndonCall.Raise(Guid.CreateVersion7(), Site, Guid.CreateVersion7(), type, null, T0, "dana", null);
        Assert.False(AndonRules.IsEscalated(call, type, T0.AddMinutes(9.9)));
        Assert.True(AndonRules.IsEscalated(call, type, T0.AddMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(12), AndonRules.Waited(call, T0.AddMinutes(12)));

        var acknowledged = call.Acknowledge(T0.AddMinutes(12), "lee");
        Assert.False(AndonRules.IsEscalated(acknowledged, type, T0.AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(12), AndonRules.Waited(acknowledged, T0.AddMinutes(30)));
    }

    [Fact]
    public void The_view_figures_a_listed_call_as_of_now()
    {
        var dto = new AndonCallDto
        {
            Id = Guid.CreateVersion7(),
            SiteId = Site,
            LineId = Guid.CreateVersion7(),
            LineCode = "EXT-01",
            TypeId = Guid.CreateVersion7(),
            TypeCode = "MAINT",
            TypeName = "Maintenance",
            EscalateAfterMinutes = 5,
            Status = AndonStatuses.Open,
            RaisedAt = T0,
            RaisedBy = "dana",
        };
        var figured = AndonView.Figure(dto, T0.AddMinutes(6));
        Assert.Equal((true, 360), (figured.IsEscalated, figured.WaitedSeconds));
    }
}
