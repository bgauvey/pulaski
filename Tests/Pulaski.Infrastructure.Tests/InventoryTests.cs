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
        Assert.Equal((first.LotId, (string?)null, 750m), (second.LotId, second.LotNumber, second.Quantity));

        var sent = await db.SendAsync(new MoveLotCommand(first.LotId, second.Version, plant.Staging, 200m, plant.Kg));
        Assert.Equal(550m, sent.Quantity);

        var lots = await QueryAsync<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>(new SearchLotsQuery(new LotFilter(MaterialId: plant.Caps)));
        Assert.Equal([(plant.StagingCode, 200m), (plant.StoresCode, 550m)], lots.Select(l => (l.LocationCode!, l.Quantity)).OrderBy(l => l.Item1, StringComparer.Ordinal));
        Assert.All(lots, l => Assert.True(l.IsBulk));
    }

    [Fact]
    public async Task Shortages_and_the_journal_read_from_the_database()
    {
        var plant = await CreatePlantAsync();
        var lot = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-S-" + _suffix, 1000m, plant.Kg));
        await db.SendAsync(new ConsumeLotCommand(lot.LotId, plant.Job, 200m, plant.Kg));

        // The job plans 1450 kg and has used 200; 800 kg is still available.
        var report = await QueryAsync<GetShortagesQuery, ShortageReportDto>(new GetShortagesQuery(plant.Site));
        var resin = Assert.Single(report.Materials);
        Assert.Equal((plant.Resin, 800m, 1250m, 450m), (resin.MaterialId, resin.Available, resin.Demand, resin.Shortfall));
        Assert.Equal((plant.JobNumber, 450m), (resin.FirstShortJobNumber, Assert.Single(resin.Lines).Uncovered));
        Assert.Empty(report.Problems);

        var elsewhere = await QueryAsync<GetShortagesQuery, ShortageReportDto>(new GetShortagesQuery(plant.Site, EquipmentId: plant.Stores));
        Assert.Empty(elsewhere.Materials);

        // The job has no start or hold, so a horizon leaves it out.
        var soon = await QueryAsync<GetShortagesQuery, ShortageReportDto>(new GetShortagesQuery(plant.Site, HorizonDays: 3));
        Assert.Empty(soon.Materials);

        var page = await QueryAsync<ListMovementsQuery, JournalPageDto>(new ListMovementsQuery(plant.Site));
        Assert.Equal([LotEventKind.Consumed, LotEventKind.Received], page.Entries.Select(e => e.Movement.Kind));
        Assert.Equal((lot.LotId, plant.JobNumber, "test-user"), (page.Entries[0].LotId, page.Entries[0].Movement.JobNumber, page.Entries[0].Movement.ActorId));
        Assert.False(page.HasMore);

        var newest = page.Entries[0].Movement;
        var next = await QueryAsync<ListMovementsQuery, JournalPageDto>(
            new ListMovementsQuery(plant.Site, BeforeAt: newest.OccurredAt, BeforeId: newest.Id));
        Assert.Equal(LotEventKind.Received, Assert.Single(next.Entries).Movement.Kind);

        var received = await QueryAsync<ListMovementsQuery, JournalPageDto>(
            new ListMovementsQuery(plant.Site, Kind: LotEventKind.Received, LocationId: plant.Stores, Search: "R-S-" + _suffix, Actor: "test"));
        Assert.Single(received.Entries);
        var other = await QueryAsync<ListMovementsQuery, JournalPageDto>(new ListMovementsQuery(plant.Site, JobId: Guid.CreateVersion7()));
        Assert.Empty(other.Entries);
    }

    [Fact]
    public async Task Lots_split_merge_ship_correct_and_reverse_in_the_database()
    {
        var plant = await CreatePlantAsync();
        var count = await ReasonAsync("inventory_adjustment", "COUNT");
        var lot = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-M-" + _suffix, 100m, plant.Kg, "SUP-1", DateTimeOffset.UtcNow.AddDays(60)));

        var part = await db.SendAsync(new SplitLotCommand(lot.LotId, lot.Version, 30m, plant.Kg));
        Assert.Equal(($"R-M-{_suffix}-1", 30m), (part.LotNumber, part.Quantity));
        var parentOfPart = Assert.Single((await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(part.LotId))).Parents);
        Assert.Equal((lot.LotId, "split"), (parentOfPart.LotId, parentOfPart.LinkBasis));

        var merged = await db.SendAsync(new MergeLotsCommand(lot.LotId, await VersionAsync(lot.LotId), [part.LotId]));
        Assert.Equal(100m, merged.Quantity);
        Assert.Equal(LotStatus.Consumed, (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(part.LotId))).Lot.Status);

        var corrected = await db.SendAsync(new CorrectLotCommand(lot.LotId, merged.Version, "R-N-" + _suffix, "SUP-2", null, "Label misprint"));
        var shipped = await db.SendAsync(new ShipLotCommand(lot.LotId, corrected.Version, 40m, plant.Kg, "DN-" + _suffix));
        Assert.Equal(60m, shipped.Quantity);

        var detail = await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId));
        Assert.Equal(("R-N-" + _suffix, "SUP-2"), (detail.Lot.LotNumber, detail.Lot.SupplierLot));
        Assert.Equal((LotEventKind.Shipped, "DN-" + _suffix), (detail.History[0].Kind, detail.History[0].Reference));

        var reversed = await db.SendAsync(new ReverseMovementCommand(lot.LotId, shipped.Version, detail.History[0].Id, count, "Truck refused it"));
        Assert.Equal((100m, LotStatus.Available), (reversed.Quantity, reversed.Status));
        var after = await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId));
        Assert.Equal((LotEventKind.Reversed, detail.History[0].Id), (after.History[0].Kind, after.History[0].ReversesEventId));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new ReverseMovementCommand(lot.LotId, reversed.Version, after.History[0].Id, count)));
    }

    [Fact]
    public async Task A_count_and_the_reports_run_against_the_database()
    {
        var plant = await CreatePlantAsync();
        var count = await ReasonAsync("inventory_adjustment", "COUNT");
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-C1-" + _suffix, 100m, plant.Kg));
        var b = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-C2-" + _suffix, 50m, plant.Kg));

        var started = await db.SendAsync(new StartStockCountCommand(plant.Stores, "Month end"));
        var counted = await QueryAsync<GetStockCountQuery, StockCountDto>(new GetStockCountQuery(started.Id));
        Assert.Equal((2, 0, "open"), (counted.Count.LineCount, counted.Count.CountedLines, counted.Count.Status));
        Assert.StartsWith("CNT-", counted.Count.CountNumber, StringComparison.Ordinal);

        var lines = counted.Lines.ToDictionary(l => l.LotId);
        var recorded = await db.SendAsync(new RecordCountsCommand(started.Id, started.Version,
            [new(lines[a.LotId].Id, 95m), new(lines[b.LotId].Id, 50m)]));
        var result = await db.SendAsync(new PostStockCountCommand(started.Id, recorded.Version, count));
        Assert.Equal(1, result.Adjusted);
        var lot = await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(a.LotId));
        Assert.Equal((95m, LotEventKind.Scrapped, counted.Count.CountNumber), (lot.Lot.Quantity, lot.History[0].Kind, lot.History[0].Reference));
        var listed = await QueryAsync<ListStockCountsQuery, IReadOnlyList<StockCountSummaryDto>>(new ListStockCountsQuery(plant.Site));
        Assert.Equal(("posted", 2), (Assert.Single(listed).Status, listed[0].CountedLines));

        var onDate = await QueryAsync<StockOnDateQuery, IReadOnlyList<StockOnDateDto>>(new StockOnDateQuery(plant.Site, DateTimeOffset.UtcNow));
        Assert.Equal((145m, 2), (Assert.Single(onDate, r => r.MaterialId == plant.Resin).Quantity, onDate[0].LotCount));
        var before = await QueryAsync<StockOnDateQuery, IReadOnlyList<StockOnDateDto>>(new StockOnDateQuery(plant.Site, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Empty(before);

        var aging = await QueryAsync<AgingQuery, IReadOnlyList<AgingRowDto>>(new AgingQuery(plant.Site));
        var resin = Assert.Single(aging, r => r.MaterialId == plant.Resin);
        Assert.Equal((145m, 145m, 0m), (resin.UpTo30Days, resin.Total, resin.Over180Days));

        var reasons = await QueryAsync<ListAllReasonCodesQuery, IReadOnlyList<ReasonCodeAdminDto>>(new ListAllReasonCodesQuery(plant.Site));
        Assert.Contains(reasons, r => r.Code == "COUNT" && r.Kind == "inventory_adjustment" && r.IsActive);
        var created = await db.SendAsync(new CreateReasonCodeCommand(plant.Site, "hold", "LAB-" + _suffix[..4].ToUpperInvariant(), "Waiting for the lab", false, 5));
        await db.SendAsync(new UpdateReasonCodeCommand(created.Id, created.Version, "Waiting for lab results", true, 6, true));
        var changed = Assert.Single(await QueryAsync<ListAllReasonCodesQuery, IReadOnlyList<ReasonCodeAdminDto>>(new ListAllReasonCodesQuery(plant.Site)), r => r.Id == created.Id);
        Assert.Equal(("Waiting for lab results", true, plant.Site), (changed.Name, changed.RequiresComment, changed.SiteId!.Value));

        var page = await QueryAsync<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>(new SearchLotsQuery(new LotFilter(MaterialId: plant.Resin, Limit: 1)));
        var next = await QueryAsync<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>(new SearchLotsQuery(new LotFilter(MaterialId: plant.Resin, Offset: 1, Limit: 1)));
        Assert.NotEqual(Assert.Single(page).Id, Assert.Single(next).Id);
    }

    [Fact]
    public async Task A_silo_mixes_every_arrival_into_one_balance_that_traces_back_to_the_lots()
    {
        var plant = await CreatePlantAsync();
        CreatedResponse silo = await db.SendAsync(new CreateEquipmentCommand(
            plant.Site, "SILO-" + _suffix, "Resin silo", null, "work_center", "storage_zone", false, false, true, null, ComminglesLots: true));

        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, silo.Id, "R-A-" + _suffix, 600m, plant.Kg, "SUP-A"));
        await db.SendAsync(new ReceiveLotCommand(plant.Resin, silo.Id, null, 100m, plant.Kg));
        var b = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-B-" + _suffix, 300m, plant.Kg));
        var moved = await db.SendAsync(new MoveLotCommand(b.LotId, b.Version, silo.Id));

        Assert.Equal((a.LotId, (string?)null, 1000m), (moved.LotId, moved.LotNumber, moved.Quantity));
        var balance = await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(a.LotId));
        Assert.True(balance.Lot.IsBulk);
        Assert.Equal("SILO-" + _suffix, balance.Lot.LocationCode);
        Assert.Equal(2, balance.Parents.Count(p => p.LinkBasis == "merge"));

        var back = await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(a.LotId, Forward: false));
        Assert.Equal(new HashSet<string?> { "R-A-" + _suffix, "R-B-" + _suffix }, back.Where(n => n.Depth == 1).Select(n => n.LotNumber).ToHashSet());

        var locations = await QueryAsync<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>>(new ListInventoryLocationsQuery(plant.Site));
        Assert.True(Assert.Single(locations, l => l.Id == silo.Id).ComminglesLots);

        // The stores hold a separate lot now, so they can't start mixing lots.
        await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-C-" + _suffix, 5m, plant.Kg));
        var stores = await QueryAsync<GetEquipmentQuery, Contracts.EquipmentModel.EquipmentDto>(new GetEquipmentQuery(plant.Stores));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UpdateEquipmentCommand(
            plant.Stores, stores.Version, stores.Name, null, false, false, true, true, ComminglesLots: true)));
    }

    [Fact]
    public async Task One_material_locations_shelf_life_and_stock_holding_rules_run_against_the_database()
    {
        var plant = await CreatePlantAsync();
        CreatedResponse silo = await db.SendAsync(new CreateEquipmentCommand(
            plant.Site, "SILO-" + _suffix, "Resin silo", null, "work_center", "storage_zone", false, false, true, null,
            ComminglesLots: true, SingleMaterial: true));

        // Shelf life from the material's own page.
        var stock = await QueryAsync<GetMaterialStockQuery, MaterialStockDetailDto>(new GetMaterialStockQuery(plant.Resin, plant.Site));
        await db.SendAsync(new SetShelfLifeCommand(plant.Resin, stock.Material.MaterialVersion, 120));
        var lot = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-E-" + _suffix, 10m, plant.Kg));
        var expires = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId))).Lot.ExpiresAt!.Value;
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddDays(119), DateTimeOffset.UtcNow.AddDays(121));
        Assert.Equal(120, (await QueryAsync<GetMaterialStockQuery, MaterialStockDetailDto>(new GetMaterialStockQuery(plant.Resin, plant.Site))).Material.ShelfLifeDays);

        // The silo takes resin, then refuses the caps until it is empty.
        await db.SendAsync(new ReceiveLotCommand(plant.Resin, silo.Id, null, 500m, plant.Kg));
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new ReceiveLotCommand(plant.Caps, silo.Id, null, 5m, plant.Kg)));
        Assert.Contains("RESIN-" + _suffix, refused.Message, StringComparison.Ordinal);
        var locations = await QueryAsync<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>>(new ListInventoryLocationsQuery(plant.Site));
        Assert.True(Assert.Single(locations, l => l.Id == silo.Id).SingleMaterial);

        // A location with stock can't be deactivated; the stores also hold two materials, so they can't take one at a time.
        await db.SendAsync(new ReceiveLotCommand(plant.Caps, plant.Stores, null, 5m, plant.Kg));
        var stores = await QueryAsync<GetEquipmentQuery, Contracts.EquipmentModel.EquipmentDto>(new GetEquipmentQuery(plant.Stores));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UpdateEquipmentCommand(
            plant.Stores, stores.Version, stores.Name, null, false, false, true, IsActive: false)));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UpdateEquipmentCommand(
            plant.Stores, stores.Version, stores.Name, null, false, false, true, true, SingleMaterial: true)));
        Assert.True((await QueryAsync<GetEquipmentQuery, Contracts.EquipmentModel.EquipmentDto>(new GetEquipmentQuery(silo.Id))).SingleMaterial);
    }

    [Fact]
    public async Task Lots_keep_searchable_attributes_and_certificates_beside_them_and_split_lots_carry_them()
    {
        var plant = await CreatePlantAsync();
        CreatedResponse mfi = await db.SendAsync(new CreateLotAttributeCommand(plant.Site, "mfi", "Melt index", "number", null, [], true, [], 1));
        CreatedResponse grade = await db.SendAsync(new CreateLotAttributeCommand(plant.Site, "GRADE", "Grade", "choice", null, ["A", "B"], true, [], 2));
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-A-" + _suffix, 100m, plant.Kg));
        var b = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-B-" + _suffix, 100m, plant.Kg));
        var lotVersion = await VersionAsync(a.LotId);

        var set = await db.SendAsync(new SetLotAttributeCommand(a.LotId, mfi.Id, 0, "11.2"));
        await db.SendAsync(new SetLotAttributeCommand(a.LotId, grade.Id, 0, "a"));
        await db.SendAsync(new SetLotAttributeCommand(b.LotId, mfi.Id, 0, "14"));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new SetLotAttributeCommand(a.LotId, mfi.Id, 0, "12")));
        var gradeVersion = await ValueVersionAsync(a.LotId, grade.Id);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new SetLotAttributeCommand(a.LotId, grade.Id, gradeVersion, "C")));
        Assert.True(set.Version > 0);
        Assert.Equal(lotVersion, await VersionAsync(a.LotId)); // the lot itself is untouched

        var inRange = await QueryAsync<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>(
            new SearchLotsQuery(new LotFilter(SiteId: plant.Site), new LotAttributeSearch(mfi.Id, null, "10", "12")));
        Assert.Equal([a.LotId], inRange.Select(l => l.Id));
        var gradeA = await QueryAsync<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>(
            new SearchLotsQuery(new LotFilter(SiteId: plant.Site), new LotAttributeSearch(grade.Id, "A", null, null)));
        Assert.Equal([a.LotId], gradeA.Select(l => l.Id));
        await Assert.ThrowsAsync<ValidationException>(() => QueryAsync<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>(
            new SearchLotsQuery(new LotFilter(SiteId: plant.Site), new LotAttributeSearch(mfi.Id, null, "1,5", null))));

        byte[] pdf = [.. "%PDF-1.4 certificate"u8];
        CreatedResponse coa = await db.SendAsync(new AttachLotDocumentCommand(a.LotId, "coa", "CoA from supplier", "C-881", "Acme Resins", new DateOnly(2026, 10, 1), null, "coa.pdf", pdf));
        CreatedResponse link = await db.SendAsync(new AttachLotDocumentCommand(a.LotId, "test_report", "Lab report", null, null, null, "https://lab.example/r/5", null, null));
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new AttachLotDocumentCommand(a.LotId, "coa", "Not a PDF", null, null, null, null, "x.pdf", [1, 2, 3])));

        var part = await db.SendAsync(new SplitLotCommand(a.LotId, await VersionAsync(a.LotId), 30m, plant.Kg));
        var carried = await QueryAsync<GetLotRecordsQuery, LotRecordsDto>(new GetLotRecordsQuery(part.LotId));
        Assert.Equal(("11.2", "A"), (carried.Attributes.Single(x => x.Code == "MFI").Value, carried.Attributes.Single(x => x.Code == "GRADE").Value));
        Assert.Equal(new HashSet<Guid> { coa.Id, link.Id }, carried.Documents.Select(d => d.Id).ToHashSet());
        Assert.All(carried.Documents, d => Assert.Equal(("split", a.LotId), (d.Via, d.FromLotId!.Value)));

        var file = await QueryAsync<GetLotDocumentContentQuery, LotDocumentContent>(new GetLotDocumentContentQuery(coa.Id));
        Assert.Equal(("application/pdf", "coa.pdf"), (file.ContentType, file.FileName));
        Assert.Equal(pdf, file.Content);

        await db.SendAsync(new WithdrawLotDocumentCommand(link.Id, link.Version, "Wrong lot"));
        var records = await QueryAsync<GetLotRecordsQuery, LotRecordsDto>(new GetLotRecordsQuery(a.LotId));
        Assert.Equal(["current", "withdrawn"], records.Documents.Select(d => d.Status));
        Assert.Equal(("Wrong lot", 64), (records.Documents[1].WithdrawReason, records.Documents[0].Sha256!.Length));

        // A stored file is written once: the application can't change or delete it.
        await using var app = new Npgsql.NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync();
        foreach (var sql in new[] { "UPDATE core.lot_document_content SET content = '\\x00'", "DELETE FROM core.lot_document_content" })
        {
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.ExecuteAsync(sql));
            Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    private async Task<long> ValueVersionAsync(Guid lotId, Guid attributeId) =>
        (await QueryAsync<GetLotRecordsQuery, LotRecordsDto>(new GetLotRecordsQuery(lotId))).Attributes.Single(a => a.AttributeId == attributeId).Version;

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

            -- Consumption and production happen in a run (D-035): the job has been running for a day.
            UPDATE core.job SET status = 'running' WHERE id = @job;
            INSERT INTO core.job_run (job_id, equipment_id, run_no, started_at, uom_id) VALUES (@job, @line, 1, now() - interval '1 day', @m);
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
