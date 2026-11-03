using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Integrations;
using Pulaski.Domain;
using Pulaski.Domain.Integrations;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Integrations;

/// <summary>Outbound deliveries to the ERP (D-061): how an attempt is judged, and how operators resolve failures.</summary>
public class DeliveryTests
{
    private const string Sink = "webhook:test";
    private static readonly DateTimeOffset T0 = HandlerFixture.Now;

    [Fact]
    public void A_success_is_delivered_a_refusal_fails_now_and_anything_else_waits_longer_each_time()
    {
        Assert.Equal(DeliveryStatuses.Delivered, DeliveryPolicy.AfterAttempt(1, 202, null, T0).Status);

        var refused = DeliveryPolicy.AfterAttempt(1, 400, "bad lot", T0);
        Assert.Equal(DeliveryStatuses.Failed, refused.Status);
        Assert.Contains("won't help", refused.Error, StringComparison.Ordinal);

        // Timeouts, too early, too many and server errors are worth another go.
        foreach (var code in new int?[] { 408, 425, 429, 500, 503, null })
        {
            Assert.Equal(DeliveryStatuses.Pending, DeliveryPolicy.AfterAttempt(1, code, "down", T0).Status);
        }

        Assert.Equal(T0.AddSeconds(10), DeliveryPolicy.AfterAttempt(1, 503, null, T0).NextAttemptAt);
        Assert.Equal(T0.AddSeconds(40), DeliveryPolicy.AfterAttempt(3, 503, null, T0).NextAttemptAt);
        Assert.Equal(T0.AddHours(1), DeliveryPolicy.AfterAttempt(11, 503, null, T0).NextAttemptAt);
        Assert.Equal(DeliveryStatuses.Failed, DeliveryPolicy.AfterAttempt(12, 503, null, T0).Status);
    }

    [Fact]
    public async Task Only_a_failed_delivery_is_retried_and_a_skip_needs_a_reason_and_is_recorded()
    {
        var f = new HandlerFixture();
        var deliveries = f.Stores.Deliveries;
        var failed = deliveries.Add(Sink, DeliveryStatuses.Failed, attempts: 12, error: "503");
        var delivered = deliveries.Add(Sink, DeliveryStatuses.Delivered);
        var request = new FixedRequestContext("ops");
        var retry = new RetryDeliveryHandler(f.Stores.UnitOfWork, deliveries, f.Stores.Events, f.Events, request, f.Time);
        var skip = new SkipDeliveryHandler(f.Stores.UnitOfWork, deliveries, f.Stores.Events, f.Events, request, f.Time);

        await retry.HandleAsync(new RetryDeliveryCommand(failed.Id), CancellationToken.None);
        Assert.Equal((DeliveryStatuses.Pending, T0, "ops"), (deliveries.Get(failed.Id).Status, deliveries.Get(failed.Id).NextAttemptAt, deliveries.Get(failed.Id).ResolvedBy));
        Assert.Equal(EventTypes.DeliveryRetried, f.Stores.Events.Appended[^1].Type);
        await Assert.ThrowsAsync<DomainRuleException>(() => retry.HandleAsync(new RetryDeliveryCommand(failed.Id), CancellationToken.None));

        await Assert.ThrowsAsync<ValidationException>(() => skip.HandleAsync(new SkipDeliveryCommand(failed.Id, "  "), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => skip.HandleAsync(new SkipDeliveryCommand(delivered.Id, "Done by hand"), CancellationToken.None));
        await skip.HandleAsync(new SkipDeliveryCommand(failed.Id, "Booked by hand"), CancellationToken.None);
        Assert.Equal((DeliveryStatuses.Skipped, "Booked by hand"), (deliveries.Get(failed.Id).Status, deliveries.Get(failed.Id).ResolveNote));
        Assert.Equal((EventTypes.DeliverySkipped, "Booked by hand"), (f.Stores.Events.Appended[^1].Type, f.Stores.Events.Appended[^1].Reason));
        await Assert.ThrowsAsync<NotFoundException>(() => skip.HandleAsync(new SkipDeliveryCommand(Guid.CreateVersion7(), "x"), CancellationToken.None));
    }

    [Fact]
    public async Task Retry_all_puts_every_failed_delivery_back_and_says_how_many()
    {
        var f = new HandlerFixture();
        var deliveries = f.Stores.Deliveries;
        deliveries.Add(Sink, DeliveryStatuses.Failed);
        deliveries.Add(Sink, DeliveryStatuses.Failed);
        deliveries.Add("webhook:other", DeliveryStatuses.Failed);
        var handler = new RetryFailedDeliveriesHandler(f.Stores.UnitOfWork, deliveries, new Targets(), f.Stores.Events, f.Events, new FixedRequestContext(), f.Time);

        Assert.Equal(2, (await handler.HandleAsync(new RetryFailedDeliveriesCommand(), CancellationToken.None)).Retried);
        Assert.Single(f.Stores.Events.Appended);
        Assert.Equal(0, (await handler.HandleAsync(new RetryFailedDeliveriesCommand(), CancellationToken.None)).Retried);
        Assert.Single(f.Stores.Events.Appended);
    }

    [Fact]
    public void An_answer_body_acknowledges_only_with_a_known_status_and_kinds_map_to_type_prefixes()
    {
        Assert.Equal(new Acknowledgement("rejected", "ERP-1", "Unknown item"),
            Acknowledgement.FromResponse("""{"status":"Rejected","reference":"ERP-1","message":" Unknown item "}"""));
        Assert.Equal(new Acknowledgement("accepted", null, null), Acknowledgement.FromResponse("""{"status":"accepted"}"""));
        Assert.Null(Acknowledgement.FromResponse("""{"status":"maybe"}"""));
        Assert.Null(Acknowledgement.FromResponse("OK"));
        Assert.Null(Acknowledgement.FromResponse("""{"status":"""));
        Assert.Null(Acknowledgement.FromResponse(null));
        Assert.Equal("Rejected by the receiver.", new Acknowledgement("rejected", null, null).RejectionText);

        Assert.Equal(["pulaski.work_order.", "pulaski.job.", "pulaski.job.production_", "pulaski.partial_batch."],
            DeliveryKinds.PrefixesOf([DeliveryKinds.Production, DeliveryKinds.Orders]));
        Assert.Equal([DeliveryKinds.Inventory], DeliveryKinds.KindsOf(["pulaski.lot.", "pulaski.job."]));
    }

    [Fact]
    public void Feed_settings_need_an_https_address_when_on_and_values_in_range()
    {
        DeliveryFeedSettings With(bool enabled = true, string? url = "https://erp.example.com/in", string[]? kinds = null, int attempts = 12) =>
            new("erp", enabled, url, kinds ?? ["inventory"], false, attempts, 15, 5, true);

        Assert.Empty(With().Problems());
        Assert.Empty(With(url: "http://localhost:5000/in").Problems());
        Assert.Empty(With(enabled: false, url: null, kinds: []).Problems());
        Assert.Contains(With(url: null).Problems(), p => p.Contains("address", StringComparison.Ordinal));
        Assert.Contains(With(url: "http://erp.example.com/in").Problems(), p => p.Contains("https://", StringComparison.Ordinal));
        Assert.Contains(With(url: "ftp://x/y").Problems(), p => p.Contains("full https://", StringComparison.Ordinal));
        Assert.Contains(With(kinds: []).Problems(), p => p.Contains("at least one kind", StringComparison.Ordinal));
        Assert.Contains(With(kinds: ["everything"]).Problems(), p => p.Contains("'everything'", StringComparison.Ordinal));
        Assert.Contains(With(attempts: 0).Problems(), p => p.Contains("Attempts", StringComparison.Ordinal));

        // Saved settings take over from the configuration; the configuration's secret stays.
        var configured = new DeliveryTarget("webhook:erp", false, null, ["pulaski.lot."]) { SecretConfigured = true };
        var effective = DeliveryFeeds.Effective(configured, With(kinds: ["labor"]));
        Assert.Equal((true, "erp.example.com", true), (effective.Enabled, effective.TargetHost, effective.SecretConfigured));
        Assert.Equal(["pulaski.labor."], effective.EventTypes);
        Assert.Same(configured, DeliveryFeeds.Effective(configured, null));
    }

    internal sealed class Targets : IDeliveryTargets
    {
        public DeliveryTarget Erp { get; } = new(Sink, true, "erp.test", ["pulaski.lot."]);

        public DeliveryTarget? Find(string feed) => feed == "erp" ? Erp : null;
    }
}
