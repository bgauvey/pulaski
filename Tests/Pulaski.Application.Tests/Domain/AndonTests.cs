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

    [Fact]
    public void Response_times_are_by_line_and_kind_with_a_total_and_the_longest_waits()
    {
        var (ext, pack, maint, qa) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var now = T0.AddHours(2);
        AndonCallDto Call(Guid line, string lineCode, Guid type, string typeName, int answerMin, int? resolveMin) => AndonView.Figure(new AndonCallDto
        {
            Id = Guid.CreateVersion7(),
            SiteId = Site,
            LineId = line,
            LineCode = lineCode,
            TypeId = type,
            TypeCode = typeName.ToUpperInvariant(),
            TypeName = typeName,
            EscalateAfterMinutes = 10,
            Status = resolveMin is null ? AndonStatuses.Acknowledged : AndonStatuses.Resolved,
            RaisedAt = T0,
            RaisedBy = "dana",
            AcknowledgedAt = T0.AddMinutes(answerMin),
            AcknowledgedBy = "lee",
            ResolvedAt = resolveMin is { } r ? T0.AddMinutes(r) : null,
        }, now);

        var calls = new[]
        {
            Call(ext, "EXT-01", maint, "Maintenance", 2, 20),
            Call(ext, "EXT-01", maint, "Maintenance", 12, 40),
            Call(ext, "EXT-01", qa, "Quality", 4, null),
            Call(pack, "PACK1", maint, "Maintenance", 6, 30),
        };
        var open = AndonView.Figure(calls[0] with { Id = Guid.CreateVersion7(), Status = AndonStatuses.Open, AcknowledgedAt = null, AcknowledgedBy = null, ResolvedAt = null, RaisedAt = now.AddMinutes(-90) }, now);

        var times = AndonResponseTimes.Summarize([.. calls, open], T0.AddDays(-7), now);

        Assert.Equal(["EXT-01 Maintenance", "EXT-01 Quality", "PACK1 Maintenance"], times.Rows.Select(r => $"{r.LineCode} {r.TypeName}"));
        var extMaint = times.Rows[0];
        Assert.Equal((3, 2, 1), (extMaint.Calls, extMaint.Escalated, extMaint.StillOpen));
        Assert.Equal((7 * 60, 12 * 60, 30 * 60), (extMaint.AverageAnswerSeconds, extMaint.LongestAnswerSeconds, extMaint.AverageResolveSeconds));
        Assert.Null(times.Rows[1].AverageResolveSeconds);
        Assert.Equal((5, 2, 2), (times.Total!.Calls, times.Total.Escalated, times.Total.StillOpen));

        // The open call, still waiting after 90 minutes, waited longest; then the 12-minute answer.
        Assert.Equal([open.Id, calls[1].Id], times.Slowest.Take(2).Select(c => c.Id));
        Assert.Null(AndonResponseTimes.Summarize([], T0, now).Total);
    }
}
