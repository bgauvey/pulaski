using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Health;

namespace Pulaski.Infrastructure.Tests;

/// <summary>The health page's reads over the real schema (#141).</summary>
[Collection(DatabaseTests.Name)]
public sealed class SystemHealthTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToLowerInvariant();

    [Fact]
    public async Task The_reads_find_the_migration_partitions_feeds_edge_nodes_and_stale_rollups()
    {
        var store = db.Services.GetRequiredService<ISystemHealthReadStore>();
        var (_, required, has) = await store.MigrationAsync(CancellationToken.None);
        Assert.True(has, required);
        Assert.Empty(await store.MissingPartitionsAsync(CancellationToken.None));

        // A feed with a failed delivery and one waiting; a site with a day changed two days ago and not rolled up.
        var feed = "hc" + _s;
        Contracts.Common.CreatedResponse enterprise = await db.SendAsync(new Application.EquipmentModel.CreateEquipmentCommand(
            null, "HE-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        Contracts.Common.CreatedResponse site = await db.SendAsync(new Application.EquipmentModel.CreateEquipmentCommand(
            enterprise.Id, "HS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.delivery_feed (feed, kind, enabled) VALUES (@Feed, 'webhook', true);
                INSERT INTO evt.delivery (sink, event_id, event_recorded_at, tx_id, position, event_type, subject, status, created_at)
                VALUES ('webhook:' || @Feed, gen_random_uuid(), now(), '1'::xid8, 1, 't', gen_random_uuid(), 'failed', now()),
                       ('webhook:' || @Feed, gen_random_uuid(), now(), '1'::xid8, 2, 't', gen_random_uuid(), 'pending', now() - interval '1 hour');
                INSERT INTO ts.oee_rollup_day (site_id, production_date, dirty_at) VALUES (@Site, current_date - 3, now() - interval '2 days');
                """, new { Feed = feed, Site = site.Id });
        }

        var row = Assert.Single(await store.FeedsAsync(CancellationToken.None), f => f.Feed == feed);
        Assert.Equal((true, 1, 1), (row.Enabled, row.Pending, row.Failed));
        Assert.NotNull(row.OldestPending);
        var stale = Assert.Single(await store.StaleRollupsAsync(CancellationToken.None), r => string.Equals(r.SiteCode, "HS-" + _s, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, stale.Days);
        Assert.NotNull(await store.ConnectionsAsync(CancellationToken.None));
        Assert.NotNull(await store.EdgeNodesAsync(CancellationToken.None));
    }
}
