using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Inventory end to end (D-027): real SQL, as the application login, through the handlers.</summary>
[Collection(DatabaseTests.Name)]
public sealed class InventoryTests(DatabaseFixture db)
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task Lots_move_through_receipt_consumption_production_and_adjustment()
    {
        var plant = await CreatePlantAsync();
        var count = await ReasonAsync("inventory_adjustment", "COUNT");
        var hold = await ReasonAsync("hold", "QA-HOLD");

        // Receive two resin lots, one entered in grams.
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-A-" + _suffix, 100m, plant.Kg, "SUP-77", DateTimeOffset.UtcNow.AddDays(30)));
        var b = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-B-" + _suffix, 80_000m, plant.Gram));
        Assert.Equal(80m, b.Quantity);
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-A-" + _suffix, 1m, plant.Kg)));

        // Declare 60 kg of A into the job, then 400 m of profile out of it, then 10 kg of B.
        var consumed = await db.SendAsync(new ConsumeLotCommand(a.LotId, plant.Job, 60m, plant.Kg));
        Assert.Empty(consumed.Warnings);
        var made = await db.SendAsync(new ProduceLotCommand(plant.Job, plant.Staging, 400m, plant.Metre));
        Assert.Equal($"{plant.JobNumber}-01", made.LotNumber);
        await db.SendAsync(new ConsumeLotCommand(b.LotId, plant.Job, 10m, plant.Kg));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new ConsumeLotCommand(a.LotId, plant.Job, 41m, plant.Kg)));

        // Adjust, hold and move B.
        var up = await db.SendAsync(new WriteUpLotCommand(b.LotId, await VersionAsync(b.LotId), 5m, plant.Kg, count));
        Assert.Equal(75m, up.Quantity);
        var held = await db.SendAsync(new ChangeLotStatusCommand(b.LotId, up.Version, LotStatus.OnHold, hold, "Pending melt-flow test"));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new WriteOffLotCommand(b.LotId, up.Version, 5m, plant.Kg, count)));
        var off = await db.SendAsync(new WriteOffLotCommand(b.LotId, held.Version, 15m, plant.Kg, count));
        await db.SendAsync(new MoveLotCommand(b.LotId, off.Version, plant.Staging));

        // Genealogy: both inputs are parents of the output, and the trace walks it both ways.
        var forward = await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(a.LotId, Forward: true));
        Assert.Equal([(a.LotId, 0), (made.LotId, 1)], forward.Select(n => (n.LotId, n.Depth)));
        var backward = await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(made.LotId, Forward: false));
        Assert.Equal(new HashSet<Guid> { made.LotId, a.LotId, b.LotId }, backward.Select(n => n.LotId).ToHashSet());

        var lot = await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(b.LotId));
        Assert.Equal(
            [LotEventKind.Moved, LotEventKind.Scrapped, LotEventKind.StatusChanged, LotEventKind.Adjusted, LotEventKind.Consumed, LotEventKind.Received],
            lot.History.Select(h => h.Kind));
        Assert.Equal((60m, plant.StagingCode, LotStatus.OnHold), (lot.Lot.Quantity, lot.Lot.LocationCode, lot.Lot.Status));
        Assert.Equal(("QA-HOLD", "Pending melt-flow test"), (lot.History[2].ReasonCode, lot.History[2].Comment));
        Assert.Equal(plant.JobNumber, lot.History[4].JobNumber);
        Assert.Equal(made.LotId, Assert.Single(lot.Children).LotId);
        Assert.Equal("America/Chicago", lot.SiteTimeZone);

        // Every movement is a row with the quantity after it, and an event on the lot.
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var after = await connection.QueryAsync<decimal>(
            "SELECT qty_after FROM ts.lot_event WHERE lot_id = @Id ORDER BY occurred_at, id", new { Id = b.LotId });
        Assert.Equal([80m, 70m, 75m, 75m, 60m, 60m], after);
        Assert.Equal(6, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM evt.event WHERE subject = @Id AND type LIKE 'pulaski.lot.%'", new { Id = b.LotId }));
    }

    [Fact]
    public async Task Lookups_answer_stock_where_used_lots_locations_and_job_materials()
    {
        var plant = await CreatePlantAsync();
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-A-" + _suffix, 100m, plant.Kg));
        await db.SendAsync(new ConsumeLotCommand(a.LotId, plant.Job, 25m, plant.Kg));
        var made = await db.SendAsync(new ProduceLotCommand(plant.Job, plant.Staging, 300m, plant.Metre));

        var stock = await QueryAsync<ListStockQuery, IReadOnlyList<MaterialStockDto>>(
            new ListStockQuery(new StockFilter(SiteId: plant.Site, InStockOnly: true)));
        var resin = Assert.Single(stock, s => s.MaterialId == plant.Resin);
        Assert.Equal((75m, 75m, 0m, 1, 1, "kg" + _suffix), (resin.OnHand, resin.Available, resin.Held, resin.LotCount, resin.LocationCount, resin.BaseUomCode));
        Assert.DoesNotContain(stock, s => s.MaterialId == plant.Caps);
        var searched = await QueryAsync<ListStockQuery, IReadOnlyList<MaterialStockDto>>(
            new ListStockQuery(new StockFilter(Search: "RESIN_" + _suffix)));
        Assert.Empty(searched); // '_' is literal: it matches neither RESIN-… nor 'Resin …'

        var detail = await QueryAsync<GetMaterialStockQuery, MaterialStockDetailDto>(new GetMaterialStockQuery(plant.Resin, plant.Site));
        Assert.Equal(a.LotId, Assert.Single(detail.Lots).Id);
        Assert.Equal((plant.StoresCode, 75m), (Assert.Single(detail.Locations).LocationCode, detail.Locations[0].OnHand));

        var lots = await QueryAsync<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>(new SearchLotsQuery(new LotFilter(Search: "R-A-" + _suffix)));
        Assert.Equal(a.LotId, Assert.Single(lots).Id);

        var used = await QueryAsync<WhereUsedQuery, WhereUsedDto>(new WhereUsedQuery(plant.Resin));
        Assert.Equal(["released", "draft"], used.Boms.Select(u => u.VersionStatus));
        Assert.All(used.Boms, u => Assert.Equal(plant.Profile, u.MaterialId));
        Assert.Equal((1450m, 1000m, 10), (used.Boms[0].QtyPer, used.Boms[0].BaseQty, used.Boms[0].OperationNumber!.Value));
        var job = Assert.Single(used.Jobs);
        Assert.Equal((plant.JobNumber, 1450m, 25m), (job.JobNumber, job.PlannedQty, job.ConsumedQty));

        var locations = await QueryAsync<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>>(new ListInventoryLocationsQuery(plant.Site));
        Assert.Equal([(plant.StagingCode, 1), (plant.StoresCode, 1)], locations.Select(l => (l.Code, l.LotCount)).OrderBy(l => l.Code, StringComparer.Ordinal));

        var jobs = await QueryAsync<ListDeclarableJobsQuery, IReadOnlyList<DeclarableJobDto>>(new ListDeclarableJobsQuery(plant.Site, null, plant.Resin));
        Assert.True(Assert.Single(jobs).PlansComponent);

        var materials = await QueryAsync<GetJobMaterialsQuery, JobMaterialsDto>(new GetJobMaterialsQuery(plant.Job));
        var component = Assert.Single(materials.Components);
        Assert.Equal((true, 1450m, 25m), (component.IsPlanned, component.PlannedQty!.Value, component.ConsumedQty));
        Assert.Equal((made.LotId, 300m), (Assert.Single(materials.Produced).Id, materials.ProducedQty));

        var reasons = await QueryAsync<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>>(new ListReasonCodesQuery("inventory_adjustment", plant.Site));
        Assert.Contains(reasons, r => r.Code == "OTHER" && r.RequiresComment);
    }

    [Fact]
    public async Task A_material_that_isnt_lot_tracked_keeps_one_balance_per_location()
    {
        var plant = await CreatePlantAsync();

        var first = await db.SendAsync(new ReceiveLotCommand(plant.Caps, plant.Stores, null, 500m, plant.Kg));
        var second = await db.SendAsync(new ReceiveLotCommand(plant.Caps, plant.Stores, null, 250m, plant.Kg));
        Assert.Equal((first.LotId, "@" + plant.StoresCode, 750m), (second.LotId, second.LotNumber, second.Quantity));

        var sent = await db.SendAsync(new MoveLotCommand(first.LotId, second.Version, plant.Staging, 200m, plant.Kg));
        Assert.Equal(550m, sent.Quantity);

        var lots = await QueryAsync<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>(new SearchLotsQuery(new LotFilter(MaterialId: plant.Caps)));
        Assert.Equal([(plant.StagingCode, 200m), (plant.StoresCode, 550m)], lots.Select(l => (l.LocationCode!, l.Quantity)).OrderBy(l => l.Item1, StringComparer.Ordinal));
        Assert.All(lots, l => Assert.True(l.IsBulk));
    }

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    private async Task<long> VersionAsync(Guid lotId) =>
        (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lotId))).Lot.Version;

    private async Task<Guid> ReasonAsync(string kind, string code)
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM core.reason_code WHERE site_id IS NULL AND reason_kind = @kind AND code = @code", new { kind, code });
    }

    /// <summary>
    /// A site with stores, staging and a line; resin (kg, with a g unit), a profile (m, the
    /// fixture's unit) made from it by a released BOM and a draft one, end caps that aren't
    /// lot-tracked, and a planned job for the profile with resin as its component.
    /// </summary>
    private async Task<Plant> CreatePlantAsync()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(
            null, "ENT-" + _suffix, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(
            enterprise.Id, "SITE-" + _suffix, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(
            site.Id, "LINE-" + _suffix, "Line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse stores = await db.SendAsync(new CreateEquipmentCommand(
            site.Id, "RM-" + _suffix, "Stores", null, "work_center", "storage_zone", false, false, true, null));
        CreatedResponse staging = await db.SendAsync(new CreateEquipmentCommand(
            site.Id, "FG-" + _suffix, "Staging", null, "work_center", "storage_zone", false, false, true, null));

        var plant = new Plant
        {
            Site = site.Id,
            Stores = stores.Id,
            StoresCode = "RM-" + _suffix,
            Staging = staging.Id,
            StagingCode = "FG-" + _suffix,
            Metre = db.UomId,
            JobNumber = "WO-" + _suffix + "-10",
        };

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO core.uom (id, code, name, dimension, to_base_factor) VALUES
                (@kg, 'kg' || @s, 'Kilogram', 'mass', 1), (@g, 'g' || @s, 'Gram', 'mass', 0.001);
            INSERT INTO core.material (id, code, name, material_type, base_uom_id, is_lot_tracked) VALUES
                (@resin, 'RESIN-' || @s, 'Resin ' || @s, 'raw', @kg, true),
                (@profile, 'PRF-' || @s, 'Profile', 'finished', @m, true),
                (@caps, 'CAP-' || @s, 'End caps', 'packaging', @kg, false);

            INSERT INTO core.bom (id, material_id) VALUES (@bom, @profile);
            INSERT INTO core.bom_version (id, bom_id, version_number, status, base_qty, base_uom_id, released_at)
            VALUES (@v1, @bom, 1, 'released', 1000, @m, now());
            INSERT INTO core.bom_item (id, bom_version_id, line_no, component_material_id, qty_per, uom_id, consumption_method, operation_number)
            VALUES (@item, @v1, 10, @resin, 1450, @kg, 'declared', 10);
            INSERT INTO core.bom_version (id, bom_id, version_number, status, base_qty, base_uom_id, definition)
            VALUES (@v2, @bom, 2, 'draft', 1000, @m, jsonb_build_object('items', jsonb_build_array(jsonb_build_object(
                'id', gen_random_uuid(), 'lineNo', 10, 'componentMaterialId', @resin, 'qtyPer', 1500, 'uomId', @kg,
                'scrapFactor', 0, 'consumptionMethod', 'declared', 'operationNumber', 10))));

            INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id, bom_version_id)
            VALUES (@wo, @site, 'WO-' || @s, @profile, 1000, @m, @v1);
            INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id)
            VALUES (@job, @site, @wo, @jobNumber, 'production', @line, @profile, 1000, @m);
            INSERT INTO core.job_material (job_id, material_id, direction, planned_qty, uom_id, consumption_method, bom_item_id)
            VALUES (@job, @resin, 'consume', 1450, @kg, 'declared', @item);
            """,
            new
            {
                s = _suffix,
                kg = plant.Kg,
                g = plant.Gram,
                m = db.UomId,
                resin = plant.Resin,
                profile = plant.Profile,
                caps = plant.Caps,
                bom = Guid.CreateVersion7(),
                v1 = Guid.CreateVersion7(),
                v2 = Guid.CreateVersion7(),
                item = Guid.CreateVersion7(),
                wo = Guid.CreateVersion7(),
                site = site.Id,
                line = line.Id,
                job = plant.Job,
                jobNumber = plant.JobNumber,
            });
        return plant;
    }

    private sealed record Plant
    {
        public required Guid Site { get; init; }
        public required Guid Stores { get; init; }
        public required string StoresCode { get; init; }
        public required Guid Staging { get; init; }
        public required string StagingCode { get; init; }
        public required Guid Metre { get; init; }
        public required string JobNumber { get; init; }
        public Guid Kg { get; } = Guid.CreateVersion7();
        public Guid Gram { get; } = Guid.CreateVersion7();
        public Guid Resin { get; } = Guid.CreateVersion7();
        public Guid Profile { get; } = Guid.CreateVersion7();
        public Guid Caps { get; } = Guid.CreateVersion7();
        public Guid Job { get; } = Guid.CreateVersion7();
    }
}
