using Pulaski.Application.Alerts;
using Pulaski.Domain;
using Pulaski.Domain.Alerts;

namespace Pulaski.Application.Tests.Domain;

/// <summary>E-mail alerts (#121): subscriptions and what their messages say.</summary>
public class AlertTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_subscription_takes_minutes_only_for_kinds_that_have_them_with_their_default()
    {
        var (user, site) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var stops = AlertSubscription.Create(Guid.CreateVersion7(), user, site, null, AlertKind.LongStop, null);
        Assert.Equal(15, stops.Minutes);
        Assert.Equal(45, stops.Edit(45, true).Minutes);
        Assert.Throws<DomainRuleException>(() => stops.Edit(0, true));
        Assert.Throws<DomainRuleException>(() => stops.Edit(AlertSubscription.MaxMinutes + 1, true));
        Assert.False(stops.Edit(null, false).IsActive);

        Assert.Null(AlertSubscription.Create(Guid.CreateVersion7(), user, site, null, AlertKind.AndonEscalated, 30).Minutes);
        Assert.Throws<DomainRuleException>(() => AlertSubscription.Create(Guid.CreateVersion7(), user, site, null, "nonsense", null));
        Assert.Throws<DomainRuleException>(() => AlertSubscription.Create(Guid.CreateVersion7(), user, site, null, AlertKind.SpcAlert, null));
    }

    private static AlertDue Due(string kind, DateTimeOffset start, DateTimeOffset? end, string? what = null, string? note = null) => new(
        Guid.CreateVersion7(), kind, 15, Guid.CreateVersion7(), Guid.CreateVersion7(), "pulaski-local", "lee", "lee@example.com", "Lee Ong",
        Guid.CreateVersion7(), "PLT1", "America/Chicago", "EXT-01-SAW", what, note, start, end);

    [Fact]
    public void Messages_say_what_happened_in_site_time_with_a_link_when_the_ui_is_known()
    {
        var open = AlertMessages.Compose(Due(AlertKind.LongStop, T0.AddMinutes(-20), null, "Jam"), T0, "https://mes.example.com/");
        Assert.Equal("[Pulaski] EXT-01-SAW has been stopped for 20 min", open.Subject);
        Assert.Contains("stopped since 2026-11-20 07:40", open.Body, StringComparison.Ordinal);
        Assert.Contains("Reason: Jam.", open.Body, StringComparison.Ordinal);
        Assert.Contains("https://mes.example.com/machines", open.Body, StringComparison.Ordinal);

        var ended = AlertMessages.Compose(Due(AlertKind.LongStop, T0.AddMinutes(-90), T0.AddMinutes(-5)), T0, null);
        Assert.Equal("[Pulaski] EXT-01-SAW was stopped for 1 h 25 min", ended.Subject);
        Assert.DoesNotContain("http", ended.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Reason", ended.Body, StringComparison.Ordinal);

        Assert.Equal("[Pulaski] EXT-01-SAW's stop needs a reason",
            AlertMessages.Compose(Due(AlertKind.UncodedStop, T0.AddMinutes(-50), T0.AddMinutes(-40)), T0, null).Subject);

        var call = AlertMessages.Compose(Due(AlertKind.AndonEscalated, T0.AddMinutes(-12), null, "Maintenance", "Belt slipping"), T0, "https://mes.example.com");
        Assert.Equal("[Pulaski] Maintenance call on EXT-01-SAW is waiting", call.Subject);
        Assert.Contains("nobody has answered it for 12 min", call.Body, StringComparison.Ordinal);
        Assert.Contains("Note: Belt slipping", call.Body, StringComparison.Ordinal);
        Assert.Contains("https://mes.example.com/andon", call.Body, StringComparison.Ordinal);
        Assert.Contains("My alerts", call.Body, StringComparison.Ordinal);
    }
}
