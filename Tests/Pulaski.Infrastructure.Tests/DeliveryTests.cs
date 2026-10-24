using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Integrations;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Inventory;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Integrations;
using Pulaski.Domain.Integrations;
using Pulaski.Infrastructure.Integrations;

namespace Pulaski.Infrastructure.Tests;

/// <summary>The outbound relay (D-061) against the real log: every event once, in commit order, a subject's later ones held behind a failure.</summary>
[Collection(DatabaseTests.Name)]
public sealed class DeliveryTests(DatabaseFixture db)
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6];

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task Events_are_queued_once_in_order_and_a_failure_holds_only_its_subjects_later_events_until_resolved()
    {
        var relay = db.Services.GetRequiredService<DeliveryRelay>();
        var sink = TestDeliveryTargets.Sink;

        // Start the sink after what is already logged, then make movements on two lots.
        await relay.CaptureAsync(sink, ["pulaski.lot."], fromNow: true, 1000, CancellationToken.None);
        await DrainAsync(relay, sink);
        var (material, location, reason) = await PlantAsync();
        var a = await db.SendAsync(new ReceiveLotCommand(material, location, "DA-" + _suffix, 10m, db.UomId));
        var b = await db.SendAsync(new ReceiveLotCommand(material, location, "DB-" + _suffix, 10m, db.UomId));
        await db.SendAsync(new WriteUpLotCommand(a.LotId, a.Version, 1m, db.UomId, reason));

        await relay.CaptureAsync(sink, ["pulaski.lot."], fromNow: true, 1000, CancellationToken.None);
        await relay.CaptureAsync(sink, ["pulaski.lot."], fromNow: true, 1000, CancellationToken.None); // again: nothing twice
        var mine = (await relay.DueAsync(sink, DateTimeOffset.UtcNow.AddMinutes(1), 1000, CancellationToken.None))
            .Where(e => e.Subject == a.LotId || e.Subject == b.LotId).ToList();

        // A's later write-up waits behind its receipt; B's receipt is independent.
        Assert.Equal([a.LotId, b.LotId], mine.Select(e => e.Subject));
        Assert.All(mine, e => Assert.StartsWith("pulaski.lot.", e.Type, StringComparison.Ordinal));

        // A's receipt fails for good; B's is delivered. A's write-up stays held.
        var now = DateTimeOffset.UtcNow;
        await relay.RecordAsync(mine[0].DeliveryId, DeliveryPolicy.AfterAttempt(1, 400, "bad item", now), 400, now, CancellationToken.None);
        await relay.RecordAsync(mine[1].DeliveryId, DeliveryPolicy.AfterAttempt(1, 202, null, now), 202, now, CancellationToken.None);
        Assert.DoesNotContain(await relay.DueAsync(sink, now.AddMinutes(1), 1000, CancellationToken.None), e => e.Subject == a.LotId);
        var deliveries = await QueryAsync<ListDeliveriesQuery, IReadOnlyList<DeliveryDto>>(new ListDeliveriesQuery());
        Assert.Contains(deliveries, d => d.Subject == a.LotId && d.Status == DeliveryStatuses.Pending && d.IsHeld);
        Assert.Contains(deliveries, d => d.Subject == a.LotId && d.Status == DeliveryStatuses.Failed && d.LastStatusCode == 400);

        // Someone fixes it in the ERP and skips it: A's write-up is due now.
        await db.SendAsync(new SkipDeliveryCommand(mine[0].DeliveryId, "Booked by hand in the ERP"));
        var released = Assert.Single(await relay.DueAsync(sink, DateTimeOffset.UtcNow.AddMinutes(1), 1000, CancellationToken.None), e => e.Subject == a.LotId);
        Assert.Equal("pulaski.lot.written_up.v1", released.Type);
        Assert.Contains("\"entered\"", released.Data, StringComparison.Ordinal);

        var status = await QueryAsync<GetDeliveryStatusQuery, DeliveryStatusDto>(new GetDeliveryStatusQuery());
        Assert.True(status.Pending >= 1);
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        Assert.Equal("Booked by hand in the ERP", await connection.ExecuteScalarAsync<string>(
            "SELECT resolve_note FROM evt.delivery WHERE id = @Id", new { Id = mine[0].DeliveryId }));
    }

    private async Task<(Guid Material, Guid Location, Guid Reason)> PlantAsync()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "DENT-" + _suffix, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "DSITE-" + _suffix, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse stores = await db.SendAsync(new CreateEquipmentCommand(site.Id, "DRM-" + _suffix, "Stores", null, "work_center", "storage_zone", false, false, true, null));
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("DMAT-" + _suffix, "Resin", null, "raw", null, db.UomId, true, null));
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var reason = await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM core.reason_code WHERE site_id IS NULL AND reason_kind = 'inventory_adjustment' AND code = 'COUNT'");
        return (material.Id, stores.Id, reason);
    }

    // Leaves earlier tests' events delivered so the counts here are this test's.
    private static async Task DrainAsync(DeliveryRelay relay, string sink)
    {
        var now = DateTimeOffset.UtcNow.AddYears(1);
        IReadOnlyList<OutboundEvent> due;
        while ((due = await relay.DueAsync(sink, now, 1000, CancellationToken.None)).Count > 0)
        {
            foreach (var e in due)
            {
                await relay.RecordAsync(e.DeliveryId, DeliveryPolicy.AfterAttempt(1, 200, null, now), 200, now, CancellationToken.None);
            }
        }
    }
}
