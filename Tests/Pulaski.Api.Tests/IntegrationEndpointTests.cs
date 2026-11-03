using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Pulaski.Api.Integrations;
using Pulaski.Contracts.Integrations;
using Pulaski.Domain.Integrations;
using Pulaski.Domain.Security;
using Pulaski.Infrastructure.Integrations;

namespace Pulaski.Api.Tests;

/// <summary>The ERP delivery screen's routes, and what the webhook sender puts on the wire (D-061).</summary>
public sealed class IntegrationEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Operators_list_retry_and_skip_deliveries_and_viewers_only_look()
    {
        var failed = factory.Stores.Deliveries.Add(WebhookFeedOptions.SinkName, DeliveryStatuses.Failed, attempts: 12, error: "503");
        var admin = factory.CreateClient();

        var status = await admin.GetFromJsonAsync<DeliveryStatusDto>("/api/integrations/erp");
        Assert.Equal((false, 1), (status!.Enabled, status.Failed));
        var listed = await admin.GetFromJsonAsync<List<DeliveryDto>>("/api/integrations/erp/deliveries?status=failed");
        Assert.Contains(listed!, d => d.Id == failed.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/integrations/erp/deliveries?status=lost")).StatusCode);

        var auditor = await factory.CreateClientAsAsync(Roles.Auditor, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/integrations/erp/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsync($"/api/integrations/erp/deliveries/{failed.Id}/retry", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/integrations/erp/deliveries/{failed.Id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync($"/api/integrations/erp/deliveries/{failed.Id}/retry", null)).StatusCode);
        var skip = await admin.PostAsJsonAsync($"/api/integrations/erp/deliveries/{failed.Id}/skip", new SkipDeliveryRequest { Reason = "Booked by hand" });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        Assert.Equal(DeliveryStatuses.Skipped, factory.Stores.Deliveries.Get(failed.Id).Status);
    }

    [Fact]
    public async Task Feed_settings_are_saved_from_the_screen_checked_and_take_over_from_the_configuration()
    {
        var admin = factory.CreateClient();
        var settings = await admin.GetFromJsonAsync<DeliveryFeedSettingsDto>("/api/integrations/documents/settings");
        Assert.Equal((false, false, true), (settings!.IsSaved, settings.SecretConfigured, settings.IncludeFiles));
        Assert.Equal(["certificates"], settings.EventKinds);

        // On without an address, or plain http to another machine, is refused.
        var refused = await admin.PutAsJsonAsync("/api/integrations/documents/settings", new SaveDeliveryFeedSettingsRequest { Version = 0, Enabled = true, EventKinds = ["inventory"] });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/integrations/documents/settings",
            new SaveDeliveryFeedSettingsRequest { Version = 0, Enabled = true, Url = "http://erp.example.com/in", EventKinds = ["inventory"] })).StatusCode);

        var saved = await admin.PutAsJsonAsync("/api/integrations/documents/settings", new SaveDeliveryFeedSettingsRequest
        {
            Version = 0,
            Enabled = true,
            Url = "https://erp.example.com/in",
            EventKinds = ["certificates", "production"],
            MaxAttempts = 5,
            TimeoutSeconds = 10,
            PollSeconds = 3,
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var status = await admin.GetFromJsonAsync<DeliveryStatusDto>("/api/integrations/documents");
        Assert.Equal((true, "erp.example.com"), (status!.Enabled, status.TargetHost));
        Assert.Contains("pulaski.job.production_", status.EventTypes);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/integrations/documents/settings", new SaveDeliveryFeedSettingsRequest { Version = 0 })).StatusCode);

        // Only whoever manages integrations sees the full address.
        var auditor = await factory.CreateClientAsAsync(Roles.Auditor, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/integrations/documents/settings")).StatusCode);
    }

    [Fact]
    public async Task A_signed_acknowledgement_rejecting_a_delivered_event_fails_it_and_a_bad_signature_is_refused()
    {
        var delivered = factory.Stores.Deliveries.Add(WebhookFeedOptions.SinkName, DeliveryStatuses.Delivered);
        var client = factory.CreateSignedOutClient();
        var now = DateTimeOffset.UtcNow;
        HttpRequestMessage Signed(string body, string secret = "api-tests-erp-secret", DateTimeOffset? at = null)
        {
            var timestamp = (at ?? now).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/erp/acknowledgements") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Pulaski-Timestamp", timestamp);
            request.Headers.Add("X-Pulaski-Signature", WebhookSender.Signature(secret, timestamp, body));
            return request;
        }

        var body = $$"""{"eventId":"{{delivered.EventId}}","status":"rejected","reference":"ERP-9","message":"Unknown material"}""";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Signed(body, secret: "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Signed(body, at: now.AddMinutes(-10)))).StatusCode);

        var answer = await client.SendAsync(Signed(body));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var row = factory.Stores.Deliveries.Get(delivered.Id);
        Assert.Equal((DeliveryStatuses.Failed, "rejected", "ERP-9", "callback"), (row.Status, row.AckStatus, row.AckReference, row.AckSource));
        Assert.Equal("Rejected by the receiver: Unknown material", row.LastError);

        // Accepted later, after it was put right, it is delivered after all; an unknown event is not found.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed($$"""{"eventId":"{{delivered.EventId}}","status":"accepted"}"""))).StatusCode);
        Assert.Equal(DeliveryStatuses.Delivered, factory.Stores.Deliveries.Get(delivered.Id).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Signed($$"""{"eventId":"{{Guid.CreateVersion7()}}","status":"accepted"}"""))).StatusCode);
    }

    [Fact]
    public async Task The_webhook_gets_a_signed_cloudevent_keyed_by_the_event_id()
    {
        var e = new OutboundEvent
        {
            DeliveryId = Guid.CreateVersion7(),
            EventId = Guid.CreateVersion7(),
            Type = "pulaski.lot.received.v1",
            Source = "/pulaski/test",
            Subject = Guid.CreateVersion7(),
            SubjectType = "lot",
            Sequence = 3,
            OccurredAt = new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero),
            ActorId = "ops",
            Data = """{"quantity": 10}""",
        };
        var handler = new Capture();
        var sender = new WebhookSender(new HttpClient(handler));

        var (code, error, _) = await sender.SendAsync(new Uri("https://erp.test/hook"), "s3cret", e, e.OccurredAt, CancellationToken.None);

        Assert.Equal((202, (string?)null), (code, error));
        var request = handler.Request!;
        Assert.Equal(e.EventId.ToString(), request.Headers.GetValues("Idempotency-Key").Single());
        var timestamp = request.Headers.GetValues("X-Pulaski-Timestamp").Single();
        Assert.Equal(e.OccurredAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), timestamp);
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("s3cret"u8.ToArray(), Encoding.UTF8.GetBytes($"{timestamp}.{handler.Body}")));
        Assert.Equal(expected, request.Headers.GetValues("X-Pulaski-Signature").Single());
        Assert.Equal("application/cloudevents+json", request.Content!.Headers.ContentType!.MediaType);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("1.0", body.RootElement.GetProperty("specversion").GetString());
        Assert.Equal(e.Type, body.RootElement.GetProperty("type").GetString());
        Assert.Equal(e.Subject.ToString(), body.RootElement.GetProperty("subject").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("data").GetProperty("quantity").GetInt32());

        handler.Fail = true;
        var (none, why, _) = await sender.SendAsync(new Uri("https://erp.test/hook"), "s3cret", e, e.OccurredAt, CancellationToken.None);
        Assert.Null(none);
        Assert.StartsWith("Couldn't reach the receiver", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_event_types_replace_the_default_rather_than_adding_to_it()
    {
        static WebhookFeedOptions Bind(params (string Key, string Value)[] settings) => WebhookFeedOptions.From(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>($"{WebhookFeedOptions.Section}:{s.Key}", s.Value)))
                .Build());

        Assert.Equal(["pulaski.lot."], Bind(("Enabled", "true")).EventTypes);
        Assert.Equal(["pulaski.job.", "pulaski.lot.consumed."], Bind(("EventTypes:0", "pulaski.job."), ("EventTypes:1", "pulaski.lot.consumed.")).EventTypes);
    }

    [Fact]
    public void The_document_feed_takes_certificate_events_and_sends_their_files_unless_told_not_to()
    {
        static WebhookFeedOptions Documents(params (string Key, string Value)[] settings) => WebhookFeedOptions.From(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>($"{WebhookFeedOptions.DocumentsSection}:{s.Key}", s.Value)))
                .Build(),
            WebhookFeedOptions.Documents);

        var documents = Documents(("Enabled", "true"));
        Assert.Equal(["pulaski.lot_document."], documents.EventTypes);
        Assert.Equal((WebhookFeedOptions.DocumentsSinkName, true), (documents.Sink, documents.SendsFiles));
        Assert.False(Documents(("IncludeFiles", "false")).SendsFiles);
        Assert.False(WebhookFeedOptions.From(new ConfigurationBuilder().Build()).SendsFiles);

        // The file goes base64 in data.file with its name, type, size and SHA-256; the reference already in the event stays.
        var e = new OutboundEvent { EventId = Guid.CreateVersion7(), Type = "pulaski.lot_document.attached.v1", Data = """{"lotNumber":"R-1"}""" };
        var bytes = "%PDF-1.7 test"u8.ToArray();
        using var data = JsonDocument.Parse(WebhookSender.WithFile(e, "coa.pdf", "application/pdf", bytes).Data);
        var file = data.RootElement.GetProperty("file");
        Assert.Equal("R-1", data.RootElement.GetProperty("lotNumber").GetString());
        Assert.Equal(("coa.pdf", bytes.Length), (file.GetProperty("fileName").GetString(), file.GetProperty("sizeBytes").GetInt32()));
        Assert.Equal(bytes, Convert.FromBase64String(file.GetProperty("contentBase64").GetString()!));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Feeds_are_listed_added_and_searched_and_a_brokers_answer_maps_to_the_delivery_policy()
    {
        var admin = factory.CreateClient();
        var added = await admin.PostAsJsonAsync("/api/integrations", new CreateDeliveryFeedRequest { Code = "wms", Name = "Warehouse", Kind = "mqtt" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var feeds = await admin.GetFromJsonAsync<List<DeliveryFeedSummaryDto>>("/api/integrations");
        Assert.Equal(["erp", "documents", "wms"], feeds!.Select(f => f.Feed));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/integrations/nobody")).StatusCode);

        var lot = Guid.CreateVersion7();
        factory.Stores.Deliveries.Add("mqtt:wms", DeliveryStatuses.Pending, subject: lot);
        factory.Stores.Deliveries.Add("mqtt:wms", DeliveryStatuses.Pending);
        var found = await admin.GetFromJsonAsync<List<DeliveryDto>>($"/api/integrations/wms/deliveries?search={lot}&type=received");
        Assert.Equal(lot, Assert.Single(found!).Subject);

        Assert.Equal((200, (string?)null), MqttFeedSender.Code(MQTTnet.Client.MqttClientPublishReasonCode.NoMatchingSubscribers, null));
        Assert.Equal(403, MqttFeedSender.Code(MQTTnet.Client.MqttClientPublishReasonCode.NotAuthorized, null).StatusCode);
        Assert.Equal(503, MqttFeedSender.Code(MQTTnet.Client.MqttClientPublishReasonCode.UnspecifiedError, null).StatusCode);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new HttpRequestException("connection refused");
            }

            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
