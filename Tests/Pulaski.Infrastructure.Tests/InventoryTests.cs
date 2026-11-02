using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Execution;
using Pulaski.Application.Inventory;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.MachineData;

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

        // By published times the job has no start or hold, so a horizon leaves it out; the live schedule has it running now (D-058).
        var published = await QueryAsync<GetShortagesQuery, ShortageReportDto>(new GetShortagesQuery(plant.Site, HorizonDays: 3, LiveTiming: false));
        Assert.Empty(published.Materials);
        var live = await QueryAsync<GetShortagesQuery, ShortageReportDto>(new GetShortagesQuery(plant.Site, HorizonDays: 3));
        Assert.True(live.LiveTiming);
        Assert.Equal("live", Assert.Single(Assert.Single(live.Materials).Lines).TimeSource);

        // An expected receipt due now counts beside stock and covers the shortfall, without being stock.
        await db.SendAsync(new CreateExpectedReceiptCommand(plant.Site, plant.Resin, 500m, plant.Kg, DateTimeOffset.UtcNow.AddMinutes(-5), "PO-" + _suffix, null, null));
        var covered = Assert.Single((await QueryAsync<GetShortagesQuery, ShortageReportDto>(new GetShortagesQuery(plant.Site))).Materials);
        Assert.Equal((800m, 500m, 0m), (covered.Available, covered.Incoming, covered.Shortfall));
        Assert.Equal((800m, 450m), (covered.Lines[0].FromStock, covered.Lines[0].FromIncoming));

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
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new ReverseMovementCommand(lot.LotId, reversed.Version, after.History[0].Id, count, "Again")));
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

        // The difference counts from when the lot was counted, not when the count was posted (#77).
        Assert.True(lot.History[0].IsBackdated);
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
    public async Task A_silos_flow_model_decides_which_arrivals_a_trace_back_through_it_keeps_and_says_how_sure_it_is()
    {
        var plant = await CreatePlantAsync();
        CreatedResponse silo = await db.SendAsync(new CreateEquipmentCommand(
            plant.Site, "SILO-F-" + _suffix, "Resin silo", null, "work_center", "storage_zone", false, false, true, null, ComminglesLots: true));
        var balance = await db.SendAsync(new ReceiveLotCommand(plant.Resin, silo.Id, "R-FA-" + _suffix, 600m, plant.Kg));
        await db.SendAsync(new ReceiveLotCommand(plant.Resin, silo.Id, "R-FB-" + _suffix, 300m, plant.Kg));
        await db.SendAsync(new ConsumeLotCommand(balance.LotId, plant.Job, 500m, plant.Kg));
        var made = await db.SendAsync(new ProduceLotCommand(plant.Job, plant.Staging, 100m, plant.Metre));

        async Task<IReadOnlyList<LotTraceNodeDto>> BackAsync() =>
            await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(made.LotId, Forward: false));

        // Mixed (the default): both arrivals were in the silo, each an estimated share.
        var mixed = (await BackAsync()).Where(n => n.LotNumber?.StartsWith("R-F", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, mixed.Count);
        Assert.All(mixed, n => Assert.Equal(GenealogyPrecision.Estimated, n.Precision));

        // First in, first out: the 500 kg came from the first 600 kg alone.
        var location = Assert.Single(await QueryAsync<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>>(new ListInventoryLocationsQuery(plant.Site)), l => l.Id == silo.Id);
        await db.SendAsync(new SetFlowCommand(silo.Id, location.Version, FlowModels.Fifo, null));
        var fifo = Assert.Single(await BackAsync(), n => n.LotNumber?.StartsWith("R-F", StringComparison.Ordinal) == true);
        Assert.Equal(("R-FA-" + _suffix, GenealogyPrecision.Estimated), (fifo.LotNumber, fifo.Precision));

        var allocation = await QueryAsync<GetBalanceAllocationQuery, BalanceAllocationDto>(new GetBalanceAllocationQuery(balance.LotId));
        var withdrawal = Assert.Single(allocation.Withdrawals);
        Assert.Equal((500m, plant.JobNumber, "fifo"), (withdrawal.Quantity, withdrawal.JobNumber, allocation.FlowModel));
        Assert.Equal(("R-FA-" + _suffix, 500m), (Assert.Single(withdrawal.Sources).LotNumber, withdrawal.Sources[0].Quantity));
        Assert.Contains("not measured", allocation.Basis, StringComparison.Ordinal);
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
    public async Task Containers_hold_lots_move_them_together_and_every_scan_resolves_to_one_thing()
    {
        var plant = await CreatePlantAsync();
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-CA-" + _suffix, 100m, plant.Kg));
        var b = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Staging, "R-CB-" + _suffix, 50m, plant.Kg));
        CreatedResponse pallet = await db.SendAsync(new CreateContainerCommand(plant.Site, null, "pallet", plant.Stores));
        await db.SendAsync(new PackLotCommand(a.LotId, await VersionAsync(a.LotId), pallet.Id));
        await db.SendAsync(new PackLotCommand(b.LotId, await VersionAsync(b.LotId), pallet.Id));

        var container = await QueryAsync<GetContainerQuery, ContainerDto>(new GetContainerQuery(pallet.Id));
        Assert.Equal((2, $"PAL-{container.LabelNo}", $"PUL1C{container.LabelNo}"), (container.Lots.Count, container.Code, container.Barcode));
        Assert.All(container.Lots, l => Assert.Equal((plant.StoresCode, container.Code), (l.LocationCode, l.ContainerCode)));

        await db.SendAsync(new MoveContainerCommand(pallet.Id, container.Version, plant.Staging, "To the line"));
        var moved = await QueryAsync<GetContainerQuery, ContainerDto>(new GetContainerQuery(pallet.Id));
        Assert.All(moved.Lots, l => Assert.Equal(plant.StagingCode, l.LocationCode));
        var history = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(a.LotId))).History;
        Assert.Equal([LotEventKind.Moved, LotEventKind.Packed, LotEventKind.Received], history.Select(h => h.Kind));

        // A lot's own label, the container's label, and the lot number typed or read from a supplier's label.
        var lotLabel = moved.Lots.Single(l => l.Id == a.LotId).LabelNo;
        var byLabel = await QueryAsync<ResolveScanQuery, ScanResultDto>(new ResolveScanQuery(plant.Site, $"PUL1L{lotLabel}"));
        Assert.Equal(("found", a.LotId, "label"), (byLabel.Status, Assert.Single(byLabel.Matches).Id, byLabel.Matches[0].MatchedBy));
        var byContainer = await QueryAsync<ResolveScanQuery, ScanResultDto>(new ResolveScanQuery(plant.Site, moved.Barcode));
        Assert.Equal(("container", pallet.Id), (Assert.Single(byContainer.Matches).Kind, byContainer.Matches[0].Id));
        var byText = await QueryAsync<ResolveScanQuery, ScanResultDto>(new ResolveScanQuery(plant.Site, "r-cb-" + _suffix));
        Assert.Equal(("found", b.LotId), (byText.Status, Assert.Single(byText.Matches).Id));
        var location = await QueryAsync<ResolveScanQuery, ScanResultDto>(new ResolveScanQuery(plant.Site, "PUL1E:" + plant.StagingCode));
        Assert.Equal("location", Assert.Single(location.Matches).Kind);

        // Unpacked, a lot stays put; the emptied container can then be retired.
        await db.SendAsync(new UnpackLotCommand(a.LotId, await VersionAsync(a.LotId)));
        await db.SendAsync(new MoveLotCommand(b.LotId, await VersionAsync(b.LotId), plant.Stores));
        Assert.Null((await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(b.LotId))).Lot.ContainerId);
        var empty = await QueryAsync<GetContainerQuery, ContainerDto>(new GetContainerQuery(pallet.Id));
        Assert.Equal(0, empty.LotCount);
        await db.SendAsync(new RetireContainerCommand(pallet.Id, empty.Version));
    }

    [Fact]
    public async Task Back_dated_movements_change_the_past_the_same_whatever_order_they_were_entered_in()
    {
        var plant = await CreatePlantAsync();
        var now = DateTimeOffset.UtcNow;
        var count = await ReasonAsync("inventory_adjustment", "COUNT");
        DateTimeOffset Ago(double hours) => now.AddHours(-hours);

        // Received ten hours ago; ten written off two hours ago, then (entered last) twenty written off five hours ago.
        var lot = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-BD-" + _suffix, 50m, plant.Kg, Comment: "Delivery note found", EffectiveAt: Ago(10)));
        await db.SendAsync(new WriteOffLotCommand(lot.LotId, await VersionAsync(lot.LotId), 10m, plant.Kg, count, "Spill", Ago(2)));
        await db.SendAsync(new WriteOffLotCommand(lot.LotId, await VersionAsync(lot.LotId), 20m, plant.Kg, count, "Earlier spill", Ago(5)));
        await Assert.ThrowsAsync<DomainRuleException>(() => VersionThenAsync(v => new WriteOffLotCommand(lot.LotId, v, 21m, plant.Kg, count, "Too much", Ago(3))));

        async Task<decimal> ResinAt(DateTimeOffset at) =>
            (await QueryAsync<StockOnDateQuery, IReadOnlyList<StockOnDateDto>>(new StockOnDateQuery(plant.Site, at)))
                .SingleOrDefault(r => r.MaterialId == plant.Resin)?.Quantity ?? 0m;
        Assert.Equal((0m, 50m, 30m, 20m), (await ResinAt(Ago(11)), await ResinAt(Ago(6)), await ResinAt(Ago(3)), await ResinAt(DateTimeOffset.UtcNow)));

        // Aging runs from when it was received, not when it was entered.
        var aging = Assert.Single(await QueryAsync<AgingQuery, IReadOnlyList<AgingRowDto>>(new AgingQuery(plant.Site)), r => r.MaterialId == plant.Resin);
        Assert.InRange(aging.OldestAt!.Value, Ago(10).AddSeconds(-1), Ago(10).AddSeconds(1));

        // Production and consumption go into the run going on then; their genealogy takes effect then too.
        var made = await db.SendAsync(new ProduceLotCommand(plant.Job, plant.Staging, 100m, plant.Metre, Comment: "Shift report", EffectiveAt: Ago(8)));
        await db.SendAsync(new ConsumeLotCommand(lot.LotId, plant.Job, 5m, plant.Kg, "Shift report", EffectiveAt: Ago(7)));
        var detail = await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(made.LotId));
        var parent = Assert.Single(detail.Parents);
        Assert.InRange(parent.EffectiveAt, Ago(7).AddSeconds(-1), Ago(7).AddSeconds(1));
        Assert.True(parent.DerivedAt > parent.EffectiveAt);

        var history = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId))).History;
        Assert.All(history, h => Assert.True(h.IsBackdated));
        Assert.Equal(15m, (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId))).Lot.Quantity);
    }

    [Fact]
    public async Task Moves_holds_splits_shipments_and_merges_count_from_earlier_and_a_lot_shows_where_and_how_it_was()
    {
        var plant = await CreatePlantAsync();
        var hold = await ReasonAsync("hold", "QA-HOLD");
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset Ago(double hours) => now.AddHours(-hours);
        async Task<long> V(Guid lot) => await VersionAsync(lot);

        // Received at the stores ten hours ago; moved to staging at six and held at four, both entered now; released now.
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-PA-" + _suffix, 100m, plant.Kg, Comment: "Delivery", EffectiveAt: Ago(10)));
        await db.SendAsync(new MoveLotCommand(a.LotId, await V(a.LotId), plant.Staging, Comment: "Moved on the night shift", EffectiveAt: Ago(6)));
        await db.SendAsync(new ChangeLotStatusCommand(a.LotId, await V(a.LotId), LotStatus.OnHold, hold, "Lab call", Ago(4)));
        await db.SendAsync(new ChangeLotStatusCommand(a.LotId, await V(a.LotId), LotStatus.Available, Comment: "Lab cleared it"));

        // A move can count from earlier only as the lot's latest movement.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(async () =>
            await db.SendAsync(new MoveLotCommand(a.LotId, await V(a.LotId), plant.Stores, Comment: "Back again", EffectiveAt: Ago(5))));
        Assert.Contains("latest movement", refused.Message, StringComparison.Ordinal);

        async Task<LotStateAtDto> At(Guid lot, DateTimeOffset at) => await QueryAsync<GetLotStateAtQuery, LotStateAtDto>(new GetLotStateAtQuery(lot, at));
        var (early, moved, held) = (await At(a.LotId, Ago(8)), await At(a.LotId, Ago(5)), await At(a.LotId, Ago(3)));
        Assert.Equal((plant.Stores, LotStatus.Available, 100m), (early.LocationId!.Value, early.Status, early.Quantity));
        Assert.Equal((plant.Staging, LotStatus.Available), (moved.LocationId!.Value, moved.Status));
        Assert.Equal(LotStatus.OnHold, held.Status);
        Assert.False((await At(a.LotId, Ago(11))).Existed);

        // Stock on a date: where it was, and what was on hold, then.
        async Task<StockOnDateDto> Resin(DateTimeOffset at) =>
            Assert.Single(await QueryAsync<StockOnDateQuery, IReadOnlyList<StockOnDateDto>>(new StockOnDateQuery(plant.Site, at)), r => r.MaterialId == plant.Resin);
        Assert.Equal(plant.Staging, Assert.Single((await Resin(Ago(5))).Locations).LocationId);
        Assert.Equal((100m, 100m), ((await Resin(Ago(3))).Quantity, (await Resin(Ago(3))).OnHoldQuantity));

        // Another lot: 30 split off five hours ago, 20 shipped at four; a third lot merged into it at three, its latest movement.
        var b = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-PB-" + _suffix, 100m, plant.Kg, Comment: "Delivery", EffectiveAt: Ago(10)));
        var part = await db.SendAsync(new SplitLotCommand(b.LotId, await V(b.LotId), 30m, plant.Kg, null, plant.Staging, "Split for line 2", Ago(5)));
        Assert.False((await At(part.LotId, Ago(6))).Existed);
        Assert.Equal((true, 30m, plant.Staging), ((await At(part.LotId, Ago(4))).Existed, (await At(part.LotId, Ago(4))).Quantity, (await At(part.LotId, Ago(4))).LocationId!.Value));
        await Assert.ThrowsAsync<DomainRuleException>(async () =>
            await db.SendAsync(new ShipLotCommand(b.LotId, await V(b.LotId), 80m, plant.Kg, "SO-1", "Too much then", Ago(4))));
        await db.SendAsync(new ShipLotCommand(b.LotId, await V(b.LotId), 20m, plant.Kg, "SO-2", "Picked earlier", Ago(4)));
        var c = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-PC-" + _suffix, 10m, plant.Kg, Comment: "Delivery", EffectiveAt: Ago(9)));
        await db.SendAsync(new MergeLotsCommand(b.LotId, await V(b.LotId), [c.LotId], "Tipped together", Ago(3)));
        Assert.Equal((50m, 60m), ((await At(b.LotId, Ago(3.5))).Quantity, (await At(b.LotId, Ago(2))).Quantity));
        Assert.Equal(LotStatus.Consumed, (await At(c.LotId, Ago(2))).Status);

        // Lot A's status changed after two hours ago, so it can't be split as of then.
        await Assert.ThrowsAsync<DomainRuleException>(async () =>
            await db.SendAsync(new SplitLotCommand(a.LotId, await V(a.LotId), 10m, plant.Kg, null, null, "Before the release", Ago(2))));
    }

    [Fact]
    public async Task Earlier_moves_holds_shipments_packing_corrections_and_silo_merges_are_put_back_and_a_chain_undoes_in_one_step()
    {
        var plant = await CreatePlantAsync();
        var hold = await ReasonAsync("hold", "QA-HOLD");
        var fix = await ReasonAsync("inventory_adjustment", "COUNT");
        async Task<long> V(Guid lot) => await VersionAsync(lot);
        async Task<LotDto> Lot(Guid lot) => await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot));
        async Task<LotTransactionResponse> Reverse(Guid lot, Guid movement) => await db.SendAsync(new ReverseMovementCommand(lot, await V(lot), movement, fix, "Entered wrongly"));
        Guid Find(LotDto lot, string kind) => lot.History.First(h => h.Kind == kind && h.ReversesEventId is null && !h.IsReversed).Id;

        // A move with a write-off after it: the move is reversed (the lot hasn't moved since), the write-off stays.
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-RA-" + _suffix, 100m, plant.Kg));
        await db.SendAsync(new MoveLotCommand(a.LotId, await V(a.LotId), plant.Staging));
        await db.SendAsync(new WriteOffLotCommand(a.LotId, await V(a.LotId), 5m, plant.Kg, fix, "Spill"));
        await Reverse(a.LotId, Find(await Lot(a.LotId), LotEventKind.Moved));
        Assert.Equal((plant.StoresCode, 95m), ((await Lot(a.LotId)).Lot.LocationCode, (await Lot(a.LotId)).Lot.Quantity));

        // Held, then released: the hold isn't the latest status change, so it waits for the release to be reversed first.
        await db.SendAsync(new ChangeLotStatusCommand(a.LotId, await V(a.LotId), LotStatus.OnHold, hold, "Lab"));
        await db.SendAsync(new ChangeLotStatusCommand(a.LotId, await V(a.LotId), LotStatus.Available, Comment: "Cleared"));
        var holdRow = (await Lot(a.LotId)).History.First(h => h.Kind == LotEventKind.StatusChanged && h.ToStatus == LotStatus.OnHold).Id;
        await Assert.ThrowsAsync<DomainRuleException>(() => Reverse(a.LotId, holdRow));

        // A part shipment with a write-off after it comes back at its time.
        await db.SendAsync(new ShipLotCommand(a.LotId, await V(a.LotId), 20m, plant.Kg, "SO-9"));
        await db.SendAsync(new WriteOffLotCommand(a.LotId, await V(a.LotId), 5m, plant.Kg, fix, "Spill"));
        await Reverse(a.LotId, Find(await Lot(a.LotId), LotEventKind.Shipped));
        Assert.Equal(90m, (await Lot(a.LotId)).Lot.Quantity);

        // A correction is put back; packing comes undone, and unpacking is undone into the same pallet.
        await db.SendAsync(new CorrectLotCommand(a.LotId, await V(a.LotId), "R-RA2-" + _suffix, null, null, "Typo"));
        await Reverse(a.LotId, Find(await Lot(a.LotId), LotEventKind.Corrected));
        Assert.Equal("R-RA-" + _suffix, (await Lot(a.LotId)).Lot.LotNumber);
        CreatedResponse pallet = await db.SendAsync(new CreateContainerCommand(plant.Site, null, "pallet", plant.Stores));
        await db.SendAsync(new PackLotCommand(a.LotId, await V(a.LotId), pallet.Id));
        await Reverse(a.LotId, Find(await Lot(a.LotId), LotEventKind.Packed));
        Assert.Null((await Lot(a.LotId)).Lot.ContainerId);

        // A lot that joined a silo comes back out as itself: the balance gives its quantity back.
        CreatedResponse silo = await db.SendAsync(new CreateEquipmentCommand(
            plant.Site, "RSILO-" + _suffix, "Silo", null, "work_center", "storage_zone", false, false, true, null, ComminglesLots: true));
        var s = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-RS-" + _suffix, 40m, plant.Kg));
        var balance = await db.SendAsync(new MoveLotCommand(s.LotId, await V(s.LotId), silo.Id));
        await Reverse(s.LotId, Find(await Lot(s.LotId), LotEventKind.Merged));
        Assert.Equal((40m, LotStatus.Available, 0m), ((await Lot(s.LotId)).Lot.Quantity, (await Lot(s.LotId)).Lot.Status, (await Lot(balance.LotId)).Lot.Quantity));

        // A chain: received, written off, split, the part written off. The receipt alone can't go (its stock was used); its chain can.
        var c = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-RC-" + _suffix, 100m, plant.Kg));
        await db.SendAsync(new WriteOffLotCommand(c.LotId, await V(c.LotId), 10m, plant.Kg, fix, "Spill"));
        var part = await db.SendAsync(new SplitLotCommand(c.LotId, await V(c.LotId), 30m, plant.Kg, null, plant.Staging));
        await db.SendAsync(new WriteOffLotCommand(part.LotId, await V(part.LotId), 5m, plant.Kg, fix, "Spill"));
        var receipt = Find(await Lot(c.LotId), LotEventKind.Received);
        await Assert.ThrowsAsync<DomainRuleException>(() => Reverse(c.LotId, receipt));

        var check = await db.SendAsync(new ReverseChainCommand(c.LotId, receipt, fix, "Wrong delivery", DryRun: true));
        Assert.Equal((false, true, 4), (check.Applied, check.Reversible, check.Steps.Count));
        Assert.Equal([LotEventKind.Scrapped, LotEventKind.Split, LotEventKind.Scrapped, LotEventKind.Received], check.Steps.Select(x => x.Kind));
        Assert.Equal(60m, (await Lot(c.LotId)).Lot.Quantity);

        await Assert.ThrowsAsync<ConflictException>(() =>
            db.SendAsync(new ReverseChainCommand(c.LotId, receipt, fix, "Wrong delivery", DryRun: false, ExpectedSteps: [.. check.Steps.Skip(1).Select(x => x.EventId)])));
        var done = await db.SendAsync(new ReverseChainCommand(c.LotId, receipt, fix, "Wrong delivery", DryRun: false, ExpectedSteps: [.. check.Steps.Select(x => x.EventId)]));
        Assert.True(done.Applied);
        Assert.Equal((LotStatus.Cancelled, 0m), ((await Lot(c.LotId)).Lot.Status, (await Lot(c.LotId)).Lot.Quantity));
        Assert.Equal(LotStatus.Cancelled, (await Lot(part.LotId)).Lot.Status);
    }

    private async Task VersionThenAsync(Func<long, WriteOffLotCommand> command) =>
        await db.SendAsync(command(await VersionAsync(command(0).LotId)));

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
    [Fact]
    public async Task Declarations_are_reversed_with_compensating_movements_and_a_report_takes_back_its_lots_and_counts()
    {
        var plant = await CreatePlantAsync();
        var count = await ReasonAsync("inventory_adjustment", "COUNT");
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "RV-A-" + _suffix, 100m, plant.Kg));
        var consumed = await db.SendAsync(new ConsumeLotCommand(a.LotId, plant.Job, 30m, plant.Kg));
        var report = await db.SendAsync(new ReportProductionCommand(plant.Job, null, 200m, 0m, plant.Metre, null, plant.Staging, Comment: "First reel"));
        var made = report.Lots.Single();

        // Reversing the consumption gives the stock back and takes A out of what the job made from it.
        var history = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(a.LotId))).History;
        var consumption = history.Single(h => h.Kind == LotEventKind.Consumed);
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new ReverseMovementCommand(a.LotId, consumed.Version, consumption.Id, count)));
        var restored = await db.SendAsync(new ReverseMovementCommand(a.LotId, consumed.Version, consumption.Id, count, "Wrong lot scanned"));
        Assert.Equal(100m, restored.Quantity);
        var backward = await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(made.LotId, Forward: false));
        Assert.DoesNotContain(backward, n => n.LotId == a.LotId);
        var materials = await QueryAsync<GetJobMaterialsQuery, JobMaterialsDto>(new GetJobMaterialsQuery(plant.Job));
        Assert.All(materials.Components, c => Assert.Equal(0m, c.ConsumedQty));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new ReverseMovementCommand(a.LotId, restored.Version, consumption.Id, count, "Again")));

        // The produced lot is reversed with its report, not on its own.
        var produced = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(made.LotId))).History.Single(h => h.Kind == LotEventKind.Produced);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new ReverseMovementCommand(made.LotId, made.Version, produced.Id, count, "x")));

        // While part of the reel has been used, the report can't take it back; once that is reversed, it can.
        var split = await db.SendAsync(new SplitLotCommand(made.LotId, made.Version, 50m, plant.Metre));
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(
            new ReverseProductionReportCommand(plant.Job, report.ReportId, count, "Reported on the wrong job")));
        Assert.Contains("split", refused.Message, StringComparison.Ordinal);
        var splitRow = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(split.LotId))).History.Single(h => h.Kind == LotEventKind.Split);
        await db.SendAsync(new ReverseMovementCommand(split.LotId, split.Version, splitRow.Id, count, "Split by mistake"));
        Assert.Equal(LotStatus.Cancelled, (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(split.LotId))).Lot.Status);

        var reversed = await db.SendAsync(new ReverseProductionReportCommand(plant.Job, report.ReportId, count, "Reported on the wrong job"));
        Assert.Equal((0m, LotStatus.Cancelled), (reversed.Lots.Single().Quantity, reversed.Lots.Single().Status));
        var job = await QueryAsync<GetJobExecutionQuery, JobExecutionDto>(new GetJobExecutionQuery(plant.Job));
        Assert.Equal(0m, job.Runs.Sum(r => r.GoodQty));
        var listed = Assert.Single(job.Reports);
        Assert.True(listed.IsReversed);
        Assert.Equal(report.ReportId, listed.ReportId);
        Assert.Equal(0m, (await QueryAsync<GetJobMaterialsQuery, JobMaterialsDto>(new GetJobMaterialsQuery(plant.Job))).ProducedQty);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new ReverseProductionReportCommand(plant.Job, report.ReportId, count, "Again")));

        // An earlier write-up, not the latest movement, is reversed when the stock from then on allows it.
        var up = await db.SendAsync(new WriteUpLotCommand(a.LotId, restored.Version, 5m, plant.Kg, count));
        var moved = await db.SendAsync(new MoveLotCommand(a.LotId, up.Version, plant.Staging));
        var writeUp = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(a.LotId))).History.Single(h => h.Kind == LotEventKind.Adjusted);
        var undone = await db.SendAsync(new ReverseMovementCommand(a.LotId, moved.Version, writeUp.Id, count, "Counted twice"));
        Assert.Equal(100m, undone.Quantity);
    }

    [Fact]
    public async Task Merges_transfers_and_output_that_joined_a_silo_are_put_back_as_they_were()
    {
        var plant = await CreatePlantAsync();
        var count = await ReasonAsync("inventory_adjustment", "COUNT");

        // A merge comes apart: the source is back in its status with its quantity, the target down by it, the merge link ended.
        var target = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "RV-T-" + _suffix, 40m, plant.Kg));
        var source = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "RV-S-" + _suffix, 10m, plant.Kg));
        var merged = await db.SendAsync(new MergeLotsCommand(target.LotId, target.Version, [source.LotId]));
        var mergeRow = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(target.LotId))).History.First(h => h.Kind == LotEventKind.Merged);
        var unmerged = await db.SendAsync(new ReverseMovementCommand(target.LotId, merged.Version, mergeRow.Id, count, "Different supplier lots"));
        Assert.Equal(40m, unmerged.Quantity);
        var back = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(source.LotId))).Lot;
        Assert.Equal((10m, LotStatus.Available), (back.Quantity, back.Status));
        Assert.DoesNotContain(
            await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(source.LotId, Forward: true)), n => n.LotId == target.LotId);

        // A transfer between two silos' balances goes back.
        CreatedResponse siloA = await db.SendAsync(new CreateEquipmentCommand(
            plant.Site, "SILO-RA-" + _suffix, "Silo A", null, "work_center", "storage_zone", false, false, true, null, ComminglesLots: true));
        CreatedResponse siloB = await db.SendAsync(new CreateEquipmentCommand(
            plant.Site, "SILO-RB-" + _suffix, "Silo B", null, "work_center", "storage_zone", false, false, true, null, ComminglesLots: true));
        var inA = await db.SendAsync(new ReceiveLotCommand(plant.Resin, siloA.Id, null, 500m, plant.Kg));
        var sent = await db.SendAsync(new MoveLotCommand(inA.LotId, inA.Version, siloB.Id, 120m, plant.Kg));
        var outRow = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(inA.LotId))).History.First(h => h.Kind == LotEventKind.Moved);
        var returned = await db.SendAsync(new ReverseMovementCommand(inA.LotId, sent.Version, outRow.Id, count, "Wrong silo"));
        Assert.Equal(500m, returned.Quantity);
        Assert.Equal(0m, (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(outRow.RelatedLotId!.Value))).Lot.Quantity);

        // Output reported into a silo joined its balance; reversing the report takes it out of the balance again.
        var report = await db.SendAsync(new ReportProductionCommand(plant.Job, null, 300m, 0m, plant.Metre, null, siloB.Id, "RV-OUT-" + _suffix));
        var balance = report.Lot!;
        var reversed = await db.SendAsync(new ReverseProductionReportCommand(plant.Job, report.ReportId, count, "Wrong job"));
        Assert.Equal(LotStatus.Cancelled, Assert.Single(reversed.Lots).Status);
        var silo = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(balance.LotId))).Lot;
        Assert.Equal(balance.Quantity - 300m, silo.Quantity);
    }

    [Fact]
    public async Task A_made_lot_pairs_with_the_lots_in_use_and_keeps_the_ones_picked_and_level_readings_confirm_a_silo()
    {
        var plant = await CreatePlantAsync();

        // R1 in use, P1 made, R2 in use, P2 and P3 made: P3 pairs with R2 alone.
        var r1 = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "PR-1-" + _suffix, 100m, plant.Kg));
        var r2 = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "PR-2-" + _suffix, 100m, plant.Kg));
        await db.SendAsync(new ConsumeLotCommand(r1.LotId, plant.Job, 40m, plant.Kg));
        await db.SendAsync(new ProduceLotCommand(plant.Job, plant.Staging, 10m, plant.Metre));
        await db.SendAsync(new ConsumeLotCommand(r2.LotId, plant.Job, 40m, plant.Kg));
        await db.SendAsync(new ProduceLotCommand(plant.Job, plant.Staging, 10m, plant.Metre));
        var p3 = await db.SendAsync(new ProduceLotCommand(plant.Job, plant.Staging, 10m, plant.Metre));

        async Task<IReadOnlyList<LotTraceNodeDto>> BackAsync(Guid lotId) =>
            await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(lotId, Forward: false));

        var paired = Assert.Single(await BackAsync(p3.LotId), n => n.Depth == 1);
        Assert.Equal((r2.LotId, GenealogyPrecision.Estimated), (paired.LotId, paired.Precision));
        var forward = await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(r1.LotId, Forward: true));
        Assert.DoesNotContain(forward, n => n.LotId == p3.LotId);

        // Someone names R1 as what P3 was made from: only it is kept, as declared; R2 is listed as not paired.
        await db.SendAsync(new SetMadeFromCommand(p3.LotId, [r1.LotId], "Hopper not emptied"));
        var picked = Assert.Single(await BackAsync(p3.LotId), n => n.Depth == 1);
        Assert.Equal((r1.LotId, GenealogyPrecision.Declared), (picked.LotId, picked.Precision));
        var parents = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(p3.LotId))).Parents;
        Assert.Equal(2, parents.Count);
        Assert.Equal(("made_from", GenealogyPrecision.Declared), (parents.Single(p => p.LotId == r1.LotId).LinkBasis, parents.Single(p => p.LotId == r1.LotId).Precision));
        Assert.True(parents.Single(p => p.LotId == r2.LotId).Unpaired);
        var stray = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "PR-X-" + _suffix, 5m, plant.Kg));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new SetMadeFromCommand(p3.LotId, [stray.LotId], null)));

        // A report can name them as it makes the lot; picking none goes back to pairing by time.
        var report = await db.SendAsync(new ReportProductionCommand(plant.Job, null, 10m, 0m, plant.Metre, null, plant.Staging) { MadeFromLotIds = [r2.LotId] });
        Assert.Equal(r2.LotId, Assert.Single(await BackAsync(report.Lot!.LotId), n => n.Depth == 1).LotId);
        await db.SendAsync(new SetMadeFromCommand(p3.LotId, [], null));
        Assert.Equal(r2.LotId, Assert.Single(await BackAsync(p3.LotId), n => n.Depth == 1).LotId);

        // A silo with a level tag in kg: it reads empty after A was partly used, so B alone feeds what follows.
        CreatedResponse silo = await db.SendAsync(new CreateEquipmentCommand(
            plant.Site, "SILO-L-" + _suffix, "Level silo", null, "work_center", "storage_zone", false, false, true, null, ComminglesLots: true));
        var tag = Guid.CreateVersion7();
        await db.ExecuteAsync($"""
            WITH n AS (INSERT INTO core.edge_node (site_id, code, name) VALUES ('{plant.Site}', 'EDGE-{_suffix}', 'Edge') RETURNING id),
                 c AS (INSERT INTO core.edge_connection (edge_node_id, code, name, protocol) SELECT id, 'MQTT', 'Broker', 'sparkplug_b' FROM n RETURNING id)
            INSERT INTO core.equipment_tag (id, equipment_id, tag_role, connection_id, source_address, uom_id)
            SELECT '{tag}', '{silo.Id}', 'level', id, 'g/n/silo-{_suffix}:level', '{plant.Kg}' FROM c
            """);
        var balance = await db.SendAsync(new ReceiveLotCommand(plant.Resin, silo.Id, "PL-A-" + _suffix, 100m, plant.Kg));
        await db.SendAsync(new ConsumeLotCommand(balance.LotId, plant.Job, 60m, plant.Kg));
        await db.ExecuteAsync($"INSERT INTO ts.process_value (equipment_tag_id, occurred_at, value_num) VALUES ('{tag}', clock_timestamp(), 35)");
        await Task.Delay(20);
        await db.ExecuteAsync($"INSERT INTO ts.process_value (equipment_tag_id, occurred_at, value_num) VALUES ('{tag}', clock_timestamp(), 0)");
        await Task.Delay(20);
        await db.SendAsync(new ReceiveLotCommand(plant.Resin, silo.Id, "PL-B-" + _suffix, 80m, plant.Kg));
        await Task.Delay(20);
        await db.ExecuteAsync($"INSERT INTO ts.process_value (equipment_tag_id, occurred_at, value_num) VALUES ('{tag}', clock_timestamp(), 79)");
        await Task.Delay(20);
        await db.SendAsync(new ConsumeLotCommand(balance.LotId, plant.Job, 30m, plant.Kg));

        async Task<WithdrawalAllocationDto> LastAsync() =>
            (await QueryAsync<GetBalanceAllocationQuery, BalanceAllocationDto>(new GetBalanceAllocationQuery(balance.LotId))).Withdrawals[0];

        Assert.Equal(GenealogyPrecision.Estimated, (await LastAsync()).Precision);
        var location = Assert.Single(await QueryAsync<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>>(new ListInventoryLocationsQuery(plant.Site)), l => l.Id == silo.Id);
        var set = await db.SendAsync(new SetFlowCommand(silo.Id, location.Version, FlowModels.Mixed, null, EmptyLevel: 1m));
        var emptied = await LastAsync();
        Assert.Equal((GenealogyPrecision.Confirmed, "PL-B-" + _suffix), (emptied.Precision, Assert.Single(emptied.Sources).LotNumber));
        Assert.Contains("set aside", emptied.Reason, StringComparison.Ordinal);

        // With a tolerance the level is also compared with the declared stock: 79 against 80 agrees, within 0.5 it doesn't.
        await db.SendAsync(new SetFlowCommand(silo.Id, set.Version, FlowModels.Mixed, null, EmptyLevel: 1m, LevelTolerance: 2m));
        Assert.Equal(GenealogyPrecision.Confirmed, (await LastAsync()).Precision);
        location = Assert.Single(await QueryAsync<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>>(new ListInventoryLocationsQuery(plant.Site)), l => l.Id == silo.Id);
        Assert.Equal((1m, 2m), (location.EmptyLevel, location.LevelTolerance));
        await db.SendAsync(new SetFlowCommand(silo.Id, location.Version, FlowModels.Mixed, null, EmptyLevel: 1m, LevelTolerance: 0.5m));
        Assert.Equal(GenealogyPrecision.Possible, (await LastAsync()).Precision);
    }

    [Fact]
    public async Task Feed_points_consume_what_reports_backflush_share_by_load_time_and_count_and_flag_what_they_cant_cover()
    {
        var plant = await CreatePlantAsync();
        var count = await ReasonAsync("inventory_adjustment", "COUNT");
        await using var admin = await db.AdminDataSource.OpenConnectionAsync();
        var line = await admin.ExecuteScalarAsync<Guid>("SELECT equipment_id FROM core.job WHERE id = @Id", new { Id = plant.Job });
        var additive = Guid.CreateVersion7();
        await admin.ExecuteAsync("""
            INSERT INTO core.material (id, code, name, material_type, base_uom_id, is_lot_tracked) VALUES (@additive, 'ADD-' || @s, 'Additive', 'raw', @kg, true);
            UPDATE core.job_material SET consumption_method = 'time_window' WHERE job_id = @job;
            INSERT INTO core.job_material (job_id, material_id, direction, planned_qty, uom_id, consumption_method) VALUES (@job, @additive, 'consume', 10, @kg, 'backflush');
            """, new { additive, s = _suffix, kg = plant.Kg, job = plant.Job });
        CreatedResponse hopper = await db.SendAsync(new CreateEquipmentCommand(
            line, "HOP-" + _suffix, "Resin hopper", null, "equipment_module", "feed_point", false, false, true, null));
        CreatedResponse doser = await db.SendAsync(new CreateEquipmentCommand(
            line, "DOS-" + _suffix, "Additive doser", null, "equipment_module", "feed_point", false, false, true, null));

        // Resin loaded from the stores; additive received straight into the doser loads it there.
        var r1 = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "FR-1-" + _suffix, 100m, plant.Kg));
        await db.SendAsync(new LoadLotCommand(r1.LotId, r1.Version, hopper.Id));
        var a1 = await db.SendAsync(new ReceiveLotCommand(additive, doser.Id, "FA-1-" + _suffix, 50m, plant.Kg));
        var points = await QueryAsync<ListFeedPointsQuery, IReadOnlyList<FeedPointDto>>(new ListFeedPointsQuery(plant.Site));
        Assert.Equal((a1.LotId, r1.LotId), (Assert.Single(points[0].Loads).LotId, Assert.Single(points[1].Loads).LotId));

        // 100 m made: 1 kg of additive backflushed, 145 kg of resin by time from R1, which holds only 100.
        var report = await db.SendAsync(new ReportProductionCommand(plant.Job, null, 100m, 0m, plant.Metre, null, plant.Staging));
        Assert.Contains(report.Warnings, w => w.Code == "feed_shortfall");
        async Task<LotSummaryDto> LotAsync(Guid id) => (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(id))).Lot;
        Assert.Equal((0m, 49m), ((await LotAsync(r1.LotId)).Quantity, (await LotAsync(a1.LotId)).Quantity));
        var history = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(r1.LotId))).History;
        Assert.Equal(AutoSource.TimeWindow, history.First(h => h.Kind == LotEventKind.Consumed).AutoSource);
        var shortfall = Assert.Single(await QueryAsync<ListShortfallsQuery, IReadOnlyList<FeedShortfallDto>>(new ListShortfallsQuery(plant.Site)));
        Assert.Equal((45m, hopper.Id, r1.LotId, AutoSource.TimeWindow), (shortfall.Quantity, shortfall.EquipmentId, shortfall.LotId!.Value, shortfall.Source));

        // Resolved from another lot, into the job.
        var r2 = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "FR-2-" + _suffix, 60m, plant.Kg));
        await db.SendAsync(new ResolveShortfallCommand(shortfall.Id, shortfall.Version, r2.LotId, "Topped up from the stores"));
        Assert.Equal(15m, (await LotAsync(r2.LotId)).Quantity);
        Assert.Empty(await QueryAsync<ListShortfallsQuery, IReadOnlyList<FeedShortfallDto>>(new ListShortfallsQuery(plant.Site)));

        // Reversing the report puts back what it consumed automatically.
        await db.SendAsync(new ReverseProductionReportCommand(plant.Job, report.ReportId, count, "Wrong job"));
        Assert.Equal((100m, 50m), ((await LotAsync(r1.LotId)).Quantity, (await LotAsync(a1.LotId)).Quantity));

        // A consumed counter on the hopper: each count comes out of the resin loaded then, once, however often the signal comes.
        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(plant.Site, "GW-" + _suffix, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "BROKER", "Broker", EdgeProtocols.SparkplugB,
            new Dictionary<string, string> { ["host"] = "localhost", ["groupId"] = "Plant1" }));
        CreatedResponse counter = await db.SendAsync(new CreateEquipmentTagCommand(hopper.Id, TagRoles.ConsumedCount, connection.Id, $"Plant1/Ext/HOP-{_suffix}:Used", 1, 0, null));
        await admin.ExecuteAsync("UPDATE core.equipment_tag SET uom_id = @kg WHERE id = @id", new { kg = plant.Kg, id = counter.Id });
        var stream = Guid.CreateVersion7();
        var t0 = DateTimeOffset.UtcNow;
        EdgeSignalDto S(long seq, int seconds, decimal value) => new() { Sequence = seq, TagId = counter.Id, OccurredAt = t0.AddSeconds(seconds), Value = value };
        var batch = new IngestEdgeSignalsCommand(node.Id, stream, "1.0.0", [S(1, 0, 1000), S(2, 1, 1012)]);
        await db.SendAsync(batch);
        await db.SendAsync(batch);
        Assert.Equal(88m, (await LotAsync(r1.LotId)).Quantity);
        Assert.Equal(AutoSource.Counter, (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(r1.LotId))).History.First(h => h.Kind == LotEventKind.Consumed && !h.IsReversed).AutoSource);

        // With the counter, the hopper's resin isn't shared by time too; more counted than R1 holds is flagged, then dismissed.
        await db.SendAsync(new ReportProductionCommand(plant.Job, null, 10m, 0m, plant.Metre, null, plant.Staging));
        Assert.Equal(88m, (await LotAsync(r1.LotId)).Quantity);
        await db.SendAsync(new IngestEdgeSignalsCommand(node.Id, stream, "1.0.0", [S(3, 2, 1112)]));
        Assert.Equal(0m, (await LotAsync(r1.LotId)).Quantity);
        var over = Assert.Single(await QueryAsync<ListShortfallsQuery, IReadOnlyList<FeedShortfallDto>>(new ListShortfallsQuery(plant.Site)));
        Assert.Equal((12m, AutoSource.Counter), (over.Quantity, over.Source));
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new DismissShortfallCommand(over.Id, over.Version, "")));
        await db.SendAsync(new DismissShortfallCommand(over.Id, over.Version, "Counter drifted; checked the hopper"));
        Assert.Equal("Counter drifted; checked the hopper",
            Assert.Single(await QueryAsync<ListShortfallsQuery, IReadOnlyList<FeedShortfallDto>>(new ListShortfallsQuery(plant.Site, FeedShortfallStatus.Dismissed))).ResolutionNote);
    }

    [Fact]
    public async Task Under_hard_reservation_feed_points_leave_another_jobs_allocation_alone_and_use_up_their_own()
    {
        var plant = await CreatePlantAsync();
        await using var admin = await db.AdminDataSource.OpenConnectionAsync();
        var line = await admin.ExecuteScalarAsync<Guid>("SELECT equipment_id FROM core.job WHERE id = @Id", new { Id = plant.Job });
        var additive = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await admin.ExecuteAsync("""
            INSERT INTO core.material (id, code, name, material_type, base_uom_id, is_lot_tracked) VALUES (@additive, 'ADD-' || @s, 'Additive', 'raw', @kg, true);
            DELETE FROM core.job_material WHERE job_id = @job;
            INSERT INTO core.job_material (job_id, material_id, direction, planned_qty, uom_id, consumption_method) VALUES (@job, @additive, 'consume', 400, @kg, 'backflush');
            INSERT INTO core.job (id, site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id)
            SELECT @other, site_id, work_order_id, job_number || '-B', 'production', equipment_id, material_id, planned_qty, uom_id FROM core.job WHERE id = @job;
            INSERT INTO core.job_material (job_id, material_id, direction, planned_qty, uom_id, consumption_method) VALUES (@other, @additive, 'consume', 100, @kg, 'declared');
            """, new { additive, other, s = _suffix, kg = plant.Kg, job = plant.Job });
        CreatedResponse doser = await db.SendAsync(new CreateEquipmentCommand(
            line, "DOS-" + _suffix, "Additive doser", null, "equipment_module", "feed_point", false, false, true, null));
        var lot = await db.SendAsync(new ReceiveLotCommand(additive, doser.Id, "HR-1-" + _suffix, 50m, plant.Kg));

        // The other job holds 30 kg; this job 10. Hard reservation on.
        await db.SendAsync(new AllocateLotCommand(other, lot.LotId, 30m, plant.Kg));
        await db.SendAsync(new AllocateLotCommand(plant.Job, lot.LotId, 10m, plant.Kg));
        await db.SendAsync(new SetAllocationSettingsCommand(plant.Site, 0, false, true, []));

        // 100 m wants 40 kg backflushed: only the 20 kg no other job holds is taken, the rest is a shortfall; this job's allocation is used up.
        var report = await db.SendAsync(new ReportProductionCommand(plant.Job, null, 100m, 0m, plant.Metre, null, plant.Staging));
        Assert.Contains(report.Warnings, w => w.Code == "feed_shortfall");
        var detail = await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId));
        Assert.Equal(30m, detail.Lot.Quantity);
        Assert.Equal((other, 30m), (Assert.Single(detail.Allocations).JobId, detail.Allocations[0].Quantity));
        var shortfall = Assert.Single(await QueryAsync<ListShortfallsQuery, IReadOnlyList<FeedShortfallDto>>(new ListShortfallsQuery(plant.Site)));
        Assert.Equal(20m, shortfall.Quantity);
        Assert.Contains("reserved for another job", shortfall.Reason, StringComparison.Ordinal);

        // Resolving it from the same lot would take the other job's stock, so it is refused, naming the job.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new ResolveShortfallCommand(shortfall.Id, shortfall.Version, lot.LotId, "From the doser")));
        Assert.Contains(plant.JobNumber + "-B", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gs1_numbers_containers_and_scans_of_gs1_and_supplier_barcodes_find_lots_or_offer_to_receive_them()
    {
        var plant = await CreatePlantAsync();

        // A company prefix gives new containers SSCCs from the site's serial.
        await db.SendAsync(new SetLabelSettingsCommand(plant.Site, 0, LabelFormats.Gs1128, "0614141", 1, null));
        CreatedResponse pallet = await db.SendAsync(new CreateContainerCommand(plant.Site, null, "pallet", plant.Stores));
        var sscc = (await QueryAsync<GetContainerQuery, ContainerDto>(new GetContainerQuery(pallet.Id))).Sscc;
        Assert.Equal(Gs1.Sscc("0614141", 1, 1), sscc);
        var setup = await QueryAsync<GetLabelSetupQuery, LabelSetupDto>(new GetLabelSetupQuery(plant.Site));
        Assert.Equal((LabelFormats.Gs1128, 2L), (setup.LabelFormat, setup.SsccNextSerial));

        // A GTIN on the resin, kept as 14 digits and given to one material only.
        var gtin = "9501101530003";
        var resin = await QueryAsync<Application.Materials.GetMaterialQuery, Contracts.Materials.MaterialDetailDto>(new Application.Materials.GetMaterialQuery(plant.Resin));
        await db.SendAsync(new Application.Materials.SetMaterialGtinCommand(plant.Resin, resin.Material.Version, gtin));
        var caps = await QueryAsync<Application.Materials.GetMaterialQuery, Contracts.Materials.MaterialDetailDto>(new Application.Materials.GetMaterialQuery(plant.Caps));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new Application.Materials.SetMaterialGtinCommand(plant.Caps, caps.Material.Version, "09501101530003")));
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new Application.Materials.SetMaterialGtinCommand(plant.Caps, caps.Material.Version, "9501101530004")));

        async Task<ScanResultDto> ScanAsync(string code) => await QueryAsync<ResolveScanQuery, ScanResultDto>(new ResolveScanQuery(plant.Site, code));

        // A supplier's GS1-128 label for a lot not here yet offers to receive it, filled in; once received, the same scan finds it.
        var label = "]C1" + Gs1.Data([new("01", "09501101530003"), new("17", "271231"), new("3101", "002500"), new("10", "S-" + _suffix)], "\u001D");
        var offer = await ScanAsync(label);
        Assert.Equal(("receivable", plant.Resin, 250m, new DateOnly(2027, 12, 31)), (offer.Status, offer.Parsed!.MaterialId!.Value, offer.Parsed.Quantity!.Value, offer.Parsed.Expiry!.Value));
        var lot = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-G-" + _suffix, 250m, plant.Kg, "S-" + _suffix));
        var found = await ScanAsync(label);
        Assert.Equal(("found", lot.LotId, "supplier lot"), (found.Status, Assert.Single(found.Matches).Id, found.Matches[0].MatchedBy));
        Assert.Equal(pallet.Id, Assert.Single((await ScanAsync("(00)" + sscc)).Matches).Id);

        // A lot label carries the resin's GTIN.
        Assert.Equal("09501101530003", (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId))).Lot.MaterialGtin);

        // A supplier's own format, tried after GS1.
        await db.SendAsync(new SaveSupplierFormatCommand(plant.Site, null, 0, "ACME", "Acme resin", @"(?<material>[A-Za-z0-9-]+)\|(?<supplier_lot>[A-Za-z0-9-]+)\|(?<qty>\d+)", null, 0, true));
        var acme = await ScanAsync($"RESIN-{_suffix}|S-{_suffix}|25");
        Assert.Equal(("found", "ACME", lot.LotId), (acme.Status, acme.Parsed!.Source, Assert.Single(acme.Matches).Id));
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new SaveSupplierFormatCommand(plant.Site, null, 0, "BAD", "Bad", "(unclosed", null, 0, true)));

        // A printer, and the settings naming it as the default.
        CreatedResponse printer = await db.SendAsync(new SaveLabelPrinterCommand(plant.Site, null, 0, "zeb-1", "Zebra", PrintMethods.RawTcp, "10.0.0.5", 9100, null, "zpl", true));
        setup = await QueryAsync<GetLabelSetupQuery, LabelSetupDto>(new GetLabelSetupQuery(plant.Site));
        await db.SendAsync(new SetLabelSettingsCommand(plant.Site, setup.Version, LabelFormats.Gs1DataMatrix, "0614141", 1, printer.Id));
        setup = await QueryAsync<GetLabelSetupQuery, LabelSetupDto>(new GetLabelSetupQuery(plant.Site));
        Assert.Equal(("ZEB-1", printer.Id), (Assert.Single(setup.Printers).Code, setup.DefaultPrinterId!.Value));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new SetLabelSettingsCommand(plant.Site, setup.Version - 1, LabelFormats.Pulaski, null, 0, null)));
    }

    [Fact]
    public async Task Boxes_go_on_pallets_and_move_with_them_and_a_container_never_ends_up_inside_itself()
    {
        var plant = await CreatePlantAsync();
        async Task<ContainerDto> GetAsync(Guid id) => await QueryAsync<GetContainerQuery, ContainerDto>(new GetContainerQuery(id));

        CreatedResponse pallet = await db.SendAsync(new CreateContainerCommand(plant.Site, null, "pallet", plant.Stores));
        CreatedResponse box = await db.SendAsync(new CreateContainerCommand(plant.Site, null, "box", plant.Staging));
        var lot = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Staging, "R-NB-" + _suffix, 20m, plant.Kg));
        await db.SendAsync(new PackLotCommand(lot.LotId, await VersionAsync(lot.LotId), box.Id));

        // The box goes on the pallet: it and its lot move to the pallet's place.
        await db.SendAsync(new PackContainerCommand(box.Id, (await GetAsync(box.Id)).Version, pallet.Id));
        var onPallet = await GetAsync(box.Id);
        Assert.Equal((pallet.Id, plant.Stores), (onPallet.ParentContainerId!.Value, onPallet.LocationId));
        var lotNow = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId))).Lot;
        Assert.Equal((plant.StoresCode, onPallet.Code), (lotNow.LocationCode, lotNow.ContainerCode));
        var holder = await GetAsync(pallet.Id);
        Assert.Equal((1, box.Id), (holder.ContainerCount, Assert.Single(holder.Containers).Id));

        // Moving the pallet carries the box and its lot.
        await db.SendAsync(new MoveContainerCommand(pallet.Id, holder.Version, plant.Staging));
        Assert.Equal(plant.Staging, (await GetAsync(box.Id)).LocationId);
        Assert.Equal(plant.StagingCode, (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId))).Lot.LocationCode);

        // No cycles; the pallet can't be retired with the box on it; a scan of the box says where it is.
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new PackContainerCommand(pallet.Id, (await GetAsync(pallet.Id)).Version, box.Id)));
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new RetireContainerCommand(pallet.Id, (await GetAsync(pallet.Id)).Version)));
        var scan = await QueryAsync<ResolveScanQuery, ScanResultDto>(new ResolveScanQuery(plant.Site, onPallet.Barcode));
        Assert.Contains("in " + holder.Code, Assert.Single(scan.Matches).Detail, StringComparison.Ordinal);

        // Taken off, it stays where it is; moved alone, a box on a pallet leaves it.
        await db.SendAsync(new PackContainerCommand(box.Id, (await GetAsync(box.Id)).Version, null));
        Assert.Equal(((Guid?)null, plant.Staging), ((await GetAsync(box.Id)).ParentContainerId, (await GetAsync(box.Id)).LocationId));
        await db.SendAsync(new PackContainerCommand(box.Id, (await GetAsync(box.Id)).Version, pallet.Id));
        await db.SendAsync(new MoveContainerCommand(box.Id, (await GetAsync(box.Id)).Version, plant.Stores));
        Assert.Null((await GetAsync(box.Id)).ParentContainerId);
        Assert.Equal(0, (await GetAsync(pallet.Id)).ContainerCount);
    }

    [Fact]
    public async Task Partial_lots_tipped_into_a_mixing_container_join_its_balance_and_whole_lots_stay_whole_elsewhere()
    {
        var plant = await CreatePlantAsync();
        var hold = await ReasonAsync("hold", "QA-HOLD");
        async Task<ContainerDto> GetAsync(Guid id) => await QueryAsync<GetContainerQuery, ContainerDto>(new GetContainerQuery(id));
        async Task<LotSummaryDto> LotAsync(Guid id) => (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(id))).Lot;

        CreatedResponse gaylord = await db.SendAsync(new CreateContainerCommand(plant.Site, null, "bin", plant.Stores, MixesLots: true));
        CreatedResponse tote = await db.SendAsync(new CreateContainerCommand(plant.Site, null, "tote", plant.Stores));
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "MX-A-" + _suffix, 12m, plant.Kg));
        var b = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Staging, "MX-B-" + _suffix, 8m, plant.Kg));
        var c = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "MX-C-" + _suffix, 5m, plant.Kg));

        // A whole lot in an ordinary tote stays itself.
        await db.SendAsync(new PackLotCommand(c.LotId, await VersionAsync(c.LotId), tote.Id));
        Assert.Equal((5m, (await GetAsync(tote.Id)).Code), ((await LotAsync(c.LotId)).Quantity, (await LotAsync(c.LotId)).ContainerCode));

        // Two partial lots tipped into the gaylord become one balance there; B comes over from the staging area first.
        var first = await db.SendAsync(new PackLotCommand(a.LotId, await VersionAsync(a.LotId), gaylord.Id));
        var second = await db.SendAsync(new PackLotCommand(b.LotId, await VersionAsync(b.LotId), gaylord.Id));
        Assert.Equal((first.LotId, 20m, (string?)null), (second.LotId, second.Quantity, second.LotNumber));
        Assert.Contains(second.Warnings, w => w.Code == "commingled");
        var balance = await LotAsync(second.LotId);
        Assert.Equal((true, (await GetAsync(gaylord.Id)).Code, plant.StoresCode), (balance.IsBulk, balance.ContainerCode, balance.LocationCode));
        Assert.Equal((0m, LotStatus.Consumed, (string?)null), ((await LotAsync(b.LotId)).Quantity, (await LotAsync(b.LotId)).Status, (await LotAsync(b.LotId)).ContainerCode));
        var back = await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(balance.Id, Forward: false));
        Assert.Equal(new HashSet<Guid> { a.LotId, b.LotId }, back.Where(n => n.Depth == 1).Select(n => n.LotId).ToHashSet());

        // Only available stock goes in; no containers go in; its stock leaves by quantity, not by unpacking; mixing can't be switched off with stock in it.
        var held = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "MX-H-" + _suffix, 3m, plant.Kg));
        await db.SendAsync(new ChangeLotStatusCommand(held.LotId, held.Version, LotStatus.OnHold, hold, "Check"));
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new PackLotCommand(held.LotId, await VersionAsync(held.LotId), gaylord.Id)));
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new PackContainerCommand(tote.Id, (await GetAsync(tote.Id)).Version, gaylord.Id)));
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new UnpackLotCommand(balance.Id, await VersionAsync(balance.Id))));
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new SetContainerMixingCommand(gaylord.Id, (await GetAsync(gaylord.Id)).Version, false, FlowModels.Mixed, null)));

        // The gaylord goes on a pallet and travels with it, its balance too; then stock is taken out of it.
        CreatedResponse pallet = await db.SendAsync(new CreateContainerCommand(plant.Site, null, "pallet", plant.Stores));
        await db.SendAsync(new PackContainerCommand(gaylord.Id, (await GetAsync(gaylord.Id)).Version, pallet.Id));
        await db.SendAsync(new MoveContainerCommand(pallet.Id, (await GetAsync(pallet.Id)).Version, plant.Staging));
        Assert.Equal(plant.StagingCode, (await LotAsync(balance.Id)).LocationCode);
        await db.SendAsync(new ConsumeLotCommand(balance.Id, plant.Job, 6m, plant.Kg));
        Assert.Equal(14m, (await LotAsync(balance.Id)).Quantity);
        Assert.Contains(await QueryAsync<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>(new TraceLotQuery(balance.Id, Forward: false)), n => n.LotId == a.LotId);
    }

    [Fact]
    public async Task An_expected_receipt_takes_partial_receipts_until_complete_and_a_lead_time_flags_shortages_too_soon_to_cover()
    {
        var plant = await CreatePlantAsync();
        async Task<ExpectedReceiptDto> ExpectedAsync(Guid id) =>
            (await QueryAsync<ListExpectedReceiptsQuery, IReadOnlyList<ExpectedReceiptDto>>(new ListExpectedReceiptsQuery(plant.Site, IncludeClosed: true))).Single(r => r.Id == id);
        async Task<MaterialShortageDto> ResinAsync() =>
            Assert.Single((await QueryAsync<GetShortagesQuery, ShortageReportDto>(new GetShortagesQuery(plant.Site))).Materials);

        // 1000 kg expected now, in grams, against a job needing 1450 kg.
        CreatedResponse po = await db.SendAsync(new CreateExpectedReceiptCommand(plant.Site, plant.Resin, 1_000_000m, plant.Gram, DateTimeOffset.UtcNow.AddMinutes(-5), "PO-P-" + _suffix, null, null));

        // 400 kg arrives against it: counted in its own unit, it stays open for the other 600 kg, which is all the shortage check counts as coming.
        var first = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-P1-" + _suffix, 400m, plant.Kg, ExpectedReceiptId: po.Id));
        var part = await ExpectedAsync(po.Id);
        Assert.Equal(("open", 400_000m, 600_000m, first.LotId, 1), (part.Status, part.ReceivedQty, part.RemainingQty, part.ReceivedLotId!.Value, part.ReceivedLotCount));
        var resin = await ResinAsync();
        Assert.Equal((400m, 600m, 450m), (resin.Available, resin.Incoming, resin.Shortfall));

        // Cancelling it now is refused; closing it as complete keeps what arrived and expects no more.
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CancelExpectedReceiptCommand(po.Id, part.Version)));
        await db.SendAsync(new CompleteExpectedReceiptCommand(po.Id, part.Version));
        Assert.Equal(("received", 0m), ((await ExpectedAsync(po.Id)).Status, (await ExpectedAsync(po.Id)).RemainingQty));
        Assert.Equal((0m, 1050m), ((await ResinAsync()).Incoming, (await ResinAsync()).Shortfall));

        // Receipts that add up to the whole close it on their own.
        CreatedResponse po2 = await db.SendAsync(new CreateExpectedReceiptCommand(plant.Site, plant.Resin, 300m, plant.Kg, DateTimeOffset.UtcNow.AddDays(1), "PO-Q-" + _suffix, null, null));
        await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-Q1-" + _suffix, 100m, plant.Kg, ExpectedReceiptId: po2.Id));
        await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "R-Q2-" + _suffix, 200m, plant.Kg, ExpectedReceiptId: po2.Id));
        Assert.Equal(("received", 2), ((await ExpectedAsync(po2.Id)).Status, (await ExpectedAsync(po2.Id)).ReceivedLotCount));

        // A 10-day lead time: the job needs resin now, so what is short can't arrive in time.
        Assert.False((await ResinAsync()).CantArriveInTime);
        var material = await QueryAsync<Application.Materials.GetMaterialQuery, Contracts.Materials.MaterialDetailDto>(new Application.Materials.GetMaterialQuery(plant.Resin));
        await db.SendAsync(new Application.Materials.SetMaterialLeadTimeCommand(plant.Resin, material.Material.Version, 10));
        resin = await ResinAsync();
        Assert.Equal((10, true, true), (resin.LeadTimeDays!.Value, resin.CantArriveInTime, Assert.Single(resin.Lines).CantArriveInTime));
    }

    [Fact]
    public async Task Allocations_set_stock_aside_first_expiry_first_are_used_up_by_consumption_and_refresh_as_stock_changes()
    {
        var plant = await CreatePlantAsync();
        var count = await ReasonAsync("inventory_adjustment", "COUNT");
        var hold = await ReasonAsync("hold", "QA-HOLD");
        var now = DateTimeOffset.UtcNow;
        var old = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "AL-OLD-" + _suffix, 1000m, plant.Kg));
        var soon = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "AL-SOON-" + _suffix, 300m, plant.Kg, ExpiresAt: now.AddDays(10)));
        var held = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "AL-HELD-" + _suffix, 500m, plant.Kg));
        await db.SendAsync(new ChangeLotStatusCommand(held.LotId, held.Version, LotStatus.OnHold, hold, "Melt flow"));

        // The job needs 1450 kg: the lot that expires first comes first, then the oldest; the held lot is left out and says why.
        var view = await QueryAsync<GetJobAllocationsQuery, JobAllocationsDto>(new GetJobAllocationsQuery(plant.Job));
        var resin = Assert.Single(view.Components);
        Assert.Equal((1450m, 1450m), (resin.NeedQty!.Value, resin.RemainingQty));
        Assert.Equal([(soon.LotId, 300m), (old.LotId, 1000m)], resin.Recommendations.Select(r => (r.LotId, r.Quantity)));
        Assert.StartsWith("Expires", resin.Recommendations[0].Reason, StringComparison.Ordinal);
        Assert.Contains("on hold", resin.Excluded.Single(e => e.LotId == held.LotId).Reason, StringComparison.Ordinal);

        var added = await db.SendAsync(new AllocateRecommendedCommand(plant.Job));
        Assert.Equal(2, added.Allocated);
        Assert.Contains(added.Notes, n => n.Contains("still 150 kg", StringComparison.Ordinal));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new AllocateLotCommand(plant.Job, held.LotId, 10m, plant.Kg)));
        Assert.Equal(1000m, Assert.Single((await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(old.LotId))).Allocations).Quantity);

        // Consuming into the job uses its allocation up.
        await db.SendAsync(new ConsumeLotCommand(soon.LotId, plant.Job, 100m, plant.Kg));
        view = await QueryAsync<GetJobAllocationsQuery, JobAllocationsDto>(new GetJobAllocationsQuery(plant.Job));
        Assert.Equal((100m, 1200m), (view.Components[0].ConsumedQty, view.Components[0].AllocatedQty));

        // Stock written off below what is allocated shows as a problem until a refresh trims it; a release gives the rest back.
        await db.SendAsync(new WriteOffLotCommand(old.LotId, await VersionAsync(old.LotId), 400m, plant.Kg, count));
        view = await QueryAsync<GetJobAllocationsQuery, JobAllocationsDto>(new GetJobAllocationsQuery(plant.Job));
        Assert.Contains("only 600 kg", view.Components[0].Allocations.Single(a => a.LotId == old.LotId).Problem, StringComparison.Ordinal);
        var refreshed = await db.SendAsync(new RefreshAllocationsCommand(plant.Job));
        Assert.Equal(1, refreshed.Trimmed);
        view = await QueryAsync<GetJobAllocationsQuery, JobAllocationsDto>(new GetJobAllocationsQuery(plant.Job));
        Assert.All(view.Components[0].Allocations, a => Assert.Null(a.Problem));
        Assert.Equal(600m, view.Components[0].Allocations.Single(a => a.LotId == old.LotId).Quantity);

        var release = view.Components[0].Allocations.Single(a => a.LotId == soon.LotId);
        await db.SendAsync(new ReleaseAllocationCommand(plant.Job, release.Id, "Saving it for the trial"));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new ReleaseAllocationCommand(plant.Job, release.Id)));
        Assert.Empty((await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(soon.LotId))).Allocations);

        var site = await db.SendAsync(new RefreshSiteAllocationsCommand(plant.Site));
        Assert.Equal(1, site.Jobs);
    }

    [Fact]
    public async Task Approved_alternates_cover_what_the_component_cant_settings_order_lots_and_picking_lists_name_what_to_fetch()
    {
        var plant = await CreatePlantAsync();
        var alternate = Guid.CreateVersion7();
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO core.material (id, code, name, material_type, base_uom_id, is_lot_tracked)
                VALUES (@alternate, 'RESIN2-' || @s, 'Resin, second source', 'raw', @kg, true);
                INSERT INTO core.bom_item_alternate (id, bom_item_id, material_id, ratio, priority)
                SELECT @id, bom_item_id, @alternate, 1.25, 1 FROM core.job_material WHERE job_id = @job;
                """,
                new { alternate, s = _suffix, kg = plant.Kg, job = plant.Job, id = Guid.CreateVersion7() });
        }

        var now = DateTimeOffset.UtcNow;
        var whole = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "ALT-WHOLE-" + _suffix, 1000m, plant.Kg, ExpiresAt: now.AddDays(30)));
        var part = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "ALT-PART-" + _suffix, 200m, plant.Kg, ExpiresAt: now.AddDays(30)));
        var second = await db.SendAsync(new ReceiveLotCommand(alternate, plant.Stores, "ALT-2ND-" + _suffix, 1000m, plant.Kg));

        // Part lots first: of two lots expiring the same day the smaller is suggested first; what the resin can't cover, the alternate does at its ratio.
        var settings = await QueryAsync<GetAllocationSettingsQuery, AllocationSettingsDto>(new GetAllocationSettingsQuery(plant.Site));
        Assert.Equal((0L, false, false), (settings.Version, settings.KeepCurrent, settings.HardReservation));
        await db.SendAsync(new SetAllocationSettingsCommand(plant.Site, 0, true, true, [AllocationPreferences.PartLotsFirst]));
        settings = await QueryAsync<GetAllocationSettingsQuery, AllocationSettingsDto>(new GetAllocationSettingsQuery(plant.Site));
        Assert.Equal((true, true), (settings.KeepCurrent, settings.HardReservation));
        Assert.Equal([AllocationPreferences.PartLotsFirst], settings.Preferences);
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new SetAllocationSettingsCommand(plant.Site, 0, true, false, [])));

        var view = await QueryAsync<GetJobAllocationsQuery, JobAllocationsDto>(new GetJobAllocationsQuery(plant.Job));
        var resin = Assert.Single(view.Components);
        Assert.Equal([(part.LotId, 200m), (whole.LotId, 1000m), (second.LotId, 312.5m)], resin.Recommendations.Select(r => (r.LotId, r.Quantity)));
        Assert.Equal("RESIN2-" + _suffix, resin.Recommendations[2].AlternateMaterialCode);
        Assert.StartsWith("Alternate for", resin.Recommendations[2].Reason, StringComparison.Ordinal);

        // Allocated, the alternate covers the rest of the component: 1200 kg of resin and 312.5 kg of the alternate cover 1450 kg.
        await db.SendAsync(new AllocateRecommendedCommand(plant.Job));
        view = await QueryAsync<GetJobAllocationsQuery, JobAllocationsDto>(new GetJobAllocationsQuery(plant.Job));
        Assert.Equal((1450m, 0m), (view.Components[0].AllocatedQty, view.Components[0].RemainingQty));
        Assert.Equal(1.25m, view.Components[0].Allocations.Single(a => a.LotId == second.LotId).Ratio);

        // Shortages count the alternate's allocation against the component.
        var shortages = await QueryAsync<GetShortagesQuery, ShortageReportDto>(new GetShortagesQuery(plant.Site));
        Assert.DoesNotContain(shortages.Materials, m => m.MaterialId == plant.Resin && m.Shortfall > 0);

        // The picking list names all three, by location; nothing is at the line yet.
        var picking = await QueryAsync<GetPickingListQuery, PickingListDto>(new GetPickingListQuery(plant.Site, JobId: plant.Job));
        Assert.Equal(plant.JobNumber, picking.Scope);
        Assert.Equal(3, picking.Lines.Count);
        Assert.All(picking.Lines, l => Assert.Equal("RESIN-" + _suffix, l.ComponentCode));
        Assert.Equal(312.5m, picking.Lines.Single(l => l.LotId == second.LotId).Quantity);

        // Consuming the alternate is an approved substitution, and uses up its allocation.
        var consumed = await db.SendAsync(new ConsumeLotCommand(second.LotId, plant.Job, 100m, plant.Kg));
        Assert.Contains(consumed.Warnings, w => w.Code == "approved_alternate");
        Assert.DoesNotContain(consumed.Warnings, w => w.Code == "not_planned");
        view = await QueryAsync<GetJobAllocationsQuery, JobAllocationsDto>(new GetJobAllocationsQuery(plant.Job));
        Assert.Equal(212.5m, view.Components[0].Allocations.Single(a => a.LotId == second.LotId).Quantity);

        // A site keeping allocations current that has never been refreshed is due; a refresh marks it.
        await using (var scope = db.Services.CreateAsyncScope())
        {
            var refresher = scope.ServiceProvider.GetRequiredService<AllocationAutoRefresh>();
            Assert.True(await refresher.RefreshDueAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), CancellationToken.None) >= 1);
        }

        settings = await QueryAsync<GetAllocationSettingsQuery, AllocationSettingsDto>(new GetAllocationSettingsQuery(plant.Site));
        Assert.NotNull(settings.RefreshedAt);
        await using (var scope = db.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync(CancellationToken.None);
            var due = await scope.ServiceProvider.GetRequiredService<IAllocationRepository>().ListSitesDueForRefreshAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), CancellationToken.None);
            Assert.DoesNotContain(plant.Site, due);
        }
    }

    [Fact]
    public async Task Results_are_checked_against_the_released_specification_hold_lots_and_rejected_lots_move_good_to_scrap()
    {
        var plant = await CreatePlantAsync();
        var s = _suffix.ToUpperInvariant();
        CreatedResponse wall = await db.SendAsync(new Application.Quality.CreateCharacteristicCommand("WALL-" + s, "Wall thickness", null, "variable", plant.Kg, 2));
        CreatedResponse surface = await db.SendAsync(new Application.Quality.CreateCharacteristicCommand("SURF-" + s, "Surface defects", null, "attribute", null, 0));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new Application.Quality.CreateCharacteristicCommand("wall-" + s, "Again", null, "variable", null, 2)));

        // A specification for the profile: a draft is checked as it is saved, and released into fixed items.
        CreatedResponse spec = await db.SendAsync(new Application.Quality.CreateSpecCommand(plant.Profile));
        var header = await QueryAsync<Application.Quality.GetSpecQuery, Contracts.Quality.SpecDto>(new Application.Quality.GetSpecQuery(spec.Id));
        var draft = await QueryAsync<Application.Quality.GetSpecVersionQuery, Contracts.Quality.SpecVersionDto>(
            new Application.Quality.GetSpecVersionQuery(spec.Id, header.Versions[0].Id));
        Assert.Contains(draft.Problems, p => p.Code == "no_items");
        Contracts.Quality.SpecItemDto Item(Guid characteristic, decimal? lsl = null, decimal? usl = null, int? maxDefects = null, bool hold = false, int order = 0) =>
            new() { Id = Guid.CreateVersion7(), CharacteristicId = characteristic, Lsl = lsl, Usl = usl, MaxDefects = maxDefects, HoldOnFail = hold, SortOrder = order };
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new Application.Quality.SaveSpecDraftCommand(
            spec.Id, draft.Id, draft.Version, new Contracts.Quality.SpecDefinitionDto { Items = [Item(wall.Id, maxDefects: 1)] })));
        var saved = await db.SendAsync(new Application.Quality.SaveSpecDraftCommand(spec.Id, draft.Id, draft.Version, new Contracts.Quality.SpecDefinitionDto
        {
            Items = [Item(wall.Id, 3.2m, 3.4m, hold: true, order: 10), Item(surface.Id, maxDefects: 1, order: 20)],
        }));
        Assert.Empty(saved.Problems);
        await db.SendAsync(new Application.Quality.ReleaseSpecVersionCommand(spec.Id, draft.Id, saved.Version, "First issue"));
        var released = await QueryAsync<Application.Quality.GetReleasedSpecQuery, Contracts.Quality.ReleasedSpecDto>(
            new Application.Quality.GetReleasedSpecQuery(plant.Profile));
        Assert.Equal((1, "3.2 – 3.4", "at most 1 defect"), (released.VersionNumber, released.Items[0].Limits, released.Items[1].Limits));

        // A lot made in the running job: a wall thickness out of limits holds it; a surface inspection within them doesn't.
        var report = await db.SendAsync(new ReportProductionCommand(plant.Job, null, 100m, 0m, plant.Metre, null, plant.Staging));
        var lotId = report.Lots.Single().LotId;
        var failed = await db.SendAsync(new Application.Quality.RecordQualityResultCommand(
            plant.Site, wall.Id, lotId, plant.Job, null, [3.31m, 3.45m], null, null, "manual", "Caliper"));
        Assert.Equal((false, true), (failed.Conforms, failed.LotHeld));
        Assert.Contains(failed.Notes, n => n.Contains("the value is", StringComparison.Ordinal) || n.Contains("above 3.4", StringComparison.Ordinal));
        Assert.Equal(LotStatus.OnHold, (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lotId))).Lot.Status);
        var passed = await db.SendAsync(new Application.Quality.RecordQualityResultCommand(plant.Site, surface.Id, lotId, null, null, null, 50, 1, "lab", null));
        Assert.True(passed.Conforms);
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(
            new Application.Quality.RecordQualityResultCommand(plant.Site, surface.Id, lotId, null, null, [1m], null, null, "manual", null)));

        var results = await QueryAsync<Application.Quality.ListQualityResultsQuery, IReadOnlyList<Contracts.Quality.QualityResultDto>>(
            new Application.Quality.ListQualityResultsQuery(new Application.Quality.QualityResultFilter(null, LotId: lotId)));
        Assert.Equal(2, results.Count);
        var wallResult = results.Single(r => r.CharacteristicId == wall.Id);
        Assert.Equal((plant.JobNumber, 1, "manual"), (wallResult.JobNumber, wallResult.SpecVersionNumber, wallResult.Source));
        Assert.Equal([3.31m, 3.45m], wallResult.Values);

        // Left out with a reason, it is no longer listed as out of specification.
        await db.SendAsync(new Application.Quality.ExcludeQualityResultCommand(
            plant.Site, wallResult.Id, wallResult.OccurredAt, await ReasonAsync("sample_exclusion", "ENTRY-ERROR"), "Wrong gauge"));
        Assert.Empty(await QueryAsync<Application.Quality.ListQualityResultsQuery, IReadOnlyList<Contracts.Quality.QualityResultDto>>(
            new Application.Quality.ListQualityResultsQuery(new Application.Quality.QualityResultFilter(null, LotId: lotId, FailingOnly: true))));

        // Rejected on quality: the lot is written off and its 100 m become a defective count of the run, at the time it was made.
        var held = await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lotId));
        var rejected = await db.SendAsync(new Application.Quality.RejectLotCommand(lotId, held.Lot.Version, await ReasonAsync("scrap", "QUALITY"), "Wall too thick"));
        Assert.Equal(0m, rejected.Quantity);
        Assert.Contains(rejected.Warnings, w => w.Code == "counted_as_scrap");
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            var defective = await admin.ExecuteScalarAsync<decimal>(
                "SELECT sum(delta_qty) FROM ts.production_count WHERE job_id = @Job AND count_kind = 'defective'", new { Job = plant.Job });
            Assert.Equal(100m, defective);
        }

        // A number lot attribute standing for the characteristic records a result when it is set, and holds the lot when it fails.
        CreatedResponse attribute = await db.SendAsync(new CreateLotAttributeCommand(plant.Site, "WALL", "Wall", "number", null, [], true, [], 1, wall.Id));
        var second = (await db.SendAsync(new ReportProductionCommand(plant.Job, null, 50m, 0m, plant.Metre, null, plant.Staging))).Lots.Single().LotId;
        await db.SendAsync(new SetLotAttributeCommand(second, attribute.Id, 0, "3.5"));
        var fromAttribute = Assert.Single(await QueryAsync<Application.Quality.ListQualityResultsQuery, IReadOnlyList<Contracts.Quality.QualityResultDto>>(
            new Application.Quality.ListQualityResultsQuery(new Application.Quality.QualityResultFilter(null, LotId: second))));
        Assert.Equal(("lot_attribute", false), (fromAttribute.Source, fromAttribute.Conforms));
        Assert.Equal(LotStatus.OnHold, (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(second))).Lot.Status);

        // The shop order sheet carries the released specification; a new draft starts from it.
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            var order = await admin.ExecuteScalarAsync<Guid>("SELECT work_order_id FROM core.job WHERE id = @Id", new { Id = plant.Job });
            var sheet = await QueryAsync<Application.WorkOrders.GetShopOrderQuery, Contracts.WorkOrders.ShopOrderDto>(new Application.WorkOrders.GetShopOrderQuery(order));
            Assert.Equal(2, sheet.Specification!.Items.Count);
        }

        CreatedResponse next = await db.SendAsync(new Application.Quality.StartSpecDraftCommand(spec.Id));
        var copy = await QueryAsync<Application.Quality.GetSpecVersionQuery, Contracts.Quality.SpecVersionDto>(new Application.Quality.GetSpecVersionQuery(spec.Id, next.Id));
        Assert.Equal((2, 2), (copy.Number, copy.Definition.Items.Count));
        Assert.DoesNotContain(copy.Definition.Items, i => released.Items.Any(r => r.ItemId == i.Id));
    }

    [Fact]
    public async Task Attribute_values_import_all_or_nothing_export_as_they_import_and_certificates_reach_the_document_feed_with_their_files()
    {
        var plant = await CreatePlantAsync();
        CreatedResponse mfi = await db.SendAsync(new CreateLotAttributeCommand(plant.Site, "MFI", "Melt index", "number", null, [], true, [], 1));
        CreatedResponse grade = await db.SendAsync(new CreateLotAttributeCommand(plant.Site, "GRADE", "Grade", "choice", null, ["A", "B"], true, [], 2));
        var a = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "AV-A-" + _suffix, 100m, plant.Kg));
        var b = await db.SendAsync(new ReceiveLotCommand(plant.Resin, plant.Stores, "AV-B-" + _suffix, 100m, plant.Kg));
        await db.SendAsync(new SetLotAttributeCommand(b.LotId, grade.Id, 0, "B"));

        // One bad cell: nothing is saved, and the row says why.
        var csv = $"lot_number,mfi,grade\r\nav-a-{_suffix},11.2,a\r\nAV-B-{_suffix},eleven,\r\n";
        var bad = await db.SendAsync(new ImportLotAttributeValuesCommand(plant.Site, csv, DryRun: false));
        Assert.False(bad.Applied);
        Assert.Contains("MFI", Assert.Single(bad.Rows, r => r.Action == "error").Message, StringComparison.Ordinal);
        Assert.Empty(await QueryAsync<ListLotAttributeValuesQuery, IReadOnlyList<LotAttributeExportDto>>(new ListLotAttributeValuesQuery(plant.Site))
            .ContinueWith(t => t.Result.Where(v => v.LotId == a.LotId).ToList()));

        // Unknown columns are refused before any row.
        var unknown = await db.SendAsync(new ImportLotAttributeValuesCommand(plant.Site, "lot_number,colour\r\nX,red\r\n", DryRun: true));
        Assert.Contains(unknown.FileProblems, p => p.Contains("colour", StringComparison.Ordinal));

        // A dry run reports and keeps nothing; the real run sets what is given, leaves blank cells alone and clears (clear).
        csv = $"lot_number,material_code,mfi,grade\r\nAV-A-{_suffix},RESIN-{_suffix},11.2,a\r\nAV-B-{_suffix},,9.5,(clear)\r\n";
        var dry = await db.SendAsync(new ImportLotAttributeValuesCommand(plant.Site, csv, DryRun: true));
        Assert.Equal((false, 2, 0), (dry.Applied, dry.Updated, dry.Errors));
        Assert.Contains("GRADE: B → cleared", dry.Rows[1].Changes);
        var applied = await db.SendAsync(new ImportLotAttributeValuesCommand(plant.Site, csv, DryRun: false));
        Assert.True(applied.Applied);
        var values = await QueryAsync<ListLotAttributeValuesQuery, IReadOnlyList<LotAttributeExportDto>>(new ListLotAttributeValuesQuery(plant.Site));
        Assert.Equal([("MFI", "11.2"), ("GRADE", "A")], values.Where(v => v.LotId == a.LotId).Select(v => (v.AttributeCode, v.Value)));
        Assert.Equal([("MFI", "9.5")], values.Where(v => v.LotId == b.LotId).Select(v => (v.AttributeCode, v.Value)));
        Assert.Equal(0, (await db.SendAsync(new ImportLotAttributeValuesCommand(plant.Site, csv, DryRun: false))).Updated);

        // The export is the import's own format, so it reads back unchanged.
        var export = await QueryAsync<ExportLotAttributeValuesQuery, string>(new ExportLotAttributeValuesQuery(plant.Site));
        Assert.StartsWith("lot_number,material_code,MFI,GRADE\r\n", export, StringComparison.Ordinal);
        Assert.Contains($"AV-A-{_suffix},RESIN-{_suffix},11.2,A", export, StringComparison.Ordinal);
        Assert.Equal(2, (await db.SendAsync(new ImportLotAttributeValuesCommand(plant.Site, export, DryRun: true))).Unchanged);

        // Certificates: the document feed queues their events, and the stored file is read to go with the attached one.
        var relay = db.Services.GetRequiredService<Infrastructure.Integrations.DeliveryRelay>();
        const string Sink = "webhook:documents-test";
        await relay.CaptureAsync(Sink, ["pulaski.lot_document."], fromNow: true, 1000, CancellationToken.None);
        byte[] pdf = [.. "%PDF-1.4 certificate"u8];
        CreatedResponse coa = await db.SendAsync(new AttachLotDocumentCommand(a.LotId, "coa", "CoA", "C-1", "Acme", null, null, "coa.pdf", pdf));
        await db.SendAsync(new WithdrawLotDocumentCommand(coa.Id, coa.Version, "Superseded by C-2"));
        Assert.Equal(2, (await relay.CaptureAsync(Sink, ["pulaski.lot_document."], fromNow: true, 1000, CancellationToken.None)).Captured);
        var due = (await relay.DueAsync(Sink, DateTimeOffset.UtcNow.AddMinutes(1), 1000, CancellationToken.None)).Where(e => e.Subject == coa.Id).ToList();
        // The withdrawal waits behind the attachment until that is delivered: a certificate's events arrive in order.
        Assert.Equal(["pulaski.lot_document.attached.v1"], due.Select(e => e.Type));
        var file = await relay.GetDocumentFileAsync(coa.Id, CancellationToken.None);
        Assert.Equal(("coa.pdf", "application/pdf"), (file!.Value.FileName, file.Value.ContentType));
        Assert.Equal(pdf, file.Value.Content);
    }

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
