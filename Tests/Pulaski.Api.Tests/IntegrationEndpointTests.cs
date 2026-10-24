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
        var failed = factory.Stores.Deliveries.Add(ErpDeliveryOptions.SinkName, DeliveryStatuses.Failed, attempts: 12, error: "503");
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
        var sender = new ErpWebhookSender(new HttpClient(handler));

        var (code, error) = await sender.SendAsync(new Uri("https://erp.test/hook"), "s3cret", e, e.OccurredAt, CancellationToken.None);

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
        var (none, why) = await sender.SendAsync(new Uri("https://erp.test/hook"), "s3cret", e, e.OccurredAt, CancellationToken.None);
        Assert.Null(none);
        Assert.StartsWith("Couldn't reach the receiver", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_event_types_replace_the_default_rather_than_adding_to_it()
    {
        static ErpDeliveryOptions Bind(params (string Key, string Value)[] settings) => ErpDeliveryOptions.From(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>($"{ErpDeliveryOptions.Section}:{s.Key}", s.Value)))
                .Build());

        Assert.Equal(["pulaski.lot."], Bind(("Enabled", "true")).EventTypes);
        Assert.Equal(["pulaski.job.", "pulaski.lot.consumed."], Bind(("EventTypes:0", "pulaski.job."), ("EventTypes:1", "pulaski.lot.consumed.")).EventTypes);
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
