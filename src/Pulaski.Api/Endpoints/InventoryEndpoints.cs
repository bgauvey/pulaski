using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>
/// Inventory (D-027): stock by material, lots and their history and genealogy, where a material is
/// used, and the movements that change a lot.
/// </summary>
internal static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var inventory = routes.MapGroup("/inventory").WithTags("Inventory");

        inventory.MapGet("/stock", async Task<Ok<IReadOnlyList<MaterialStockDto>>> (
            Guid? siteId,
            string? search,
            Guid? materialClassId,
            Guid? locationId,
            bool? inStockOnly,
            IQueryHandler<ListStockQuery, IReadOnlyList<MaterialStockDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ListStockQuery(new StockFilter(siteId, search, materialClassId, locationId, inStockOnly ?? false)), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        inventory.MapGet("/materials/{id:guid}", async Task<Ok<MaterialStockDetailDto>> (
            Guid id,
            Guid? siteId,
            IQueryHandler<GetMaterialStockQuery, MaterialStockDetailDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMaterialStockQuery(id, siteId), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        inventory.MapPut("/materials/{id:guid}/shelf-life", async Task<Ok<VersionResponse>> (
            Guid id,
            SetShelfLifeRequest request,
            ICommandHandler<SetShelfLifeCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetShelfLifeCommand(id, request.Version, request.ShelfLifeDays), ct)))
        .RequirePermission(Permissions.Materials);

        inventory.MapGet("/materials/{id:guid}/where-used", async Task<Ok<WhereUsedDto>> (
            Guid id,
            IQueryHandler<WhereUsedQuery, WhereUsedDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new WhereUsedQuery(id), ct)))
        .RequireAny(null, Permissions.InventoryView, Permissions.MaterialsView);

        inventory.MapGet("/locations", async Task<Ok<IReadOnlyList<InventoryLocationDto>>> (
            Guid? siteId,
            IQueryHandler<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListInventoryLocationsQuery(siteId), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView, Permissions.FloorView);

        inventory.MapGet("/jobs", async Task<Ok<IReadOnlyList<DeclarableJobDto>>> (
            Guid? siteId,
            string? search,
            Guid? materialId,
            IQueryHandler<ListDeclarableJobsQuery, IReadOnlyList<DeclarableJobDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListDeclarableJobsQuery(siteId, search, materialId), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        inventory.MapGet("/shortages", async Task<Ok<ShortageReportDto>> (
            Guid siteId,
            Guid? equipmentId,
            int? horizonDays,
            IQueryHandler<GetShortagesQuery, ShortageReportDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetShortagesQuery(siteId, equipmentId, horizonDays), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        inventory.MapGet("/movements", async Task<Ok<JournalPageDto>> (
            Guid siteId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? kind,
            Guid? materialId,
            Guid? locationId,
            Guid? jobId,
            string? actor,
            string? search,
            DateTimeOffset? beforeAt,
            Guid? beforeId,
            IQueryHandler<ListMovementsQuery, JournalPageDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ListMovementsQuery(siteId, from, to, kind, materialId, locationId, jobId, actor, search, beforeAt, beforeId), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        var lots = routes.MapGroup("/lots").WithTags("Inventory");

        lots.MapGet("/", async Task<Ok<IReadOnlyList<LotSummaryDto>>> (
            Guid? siteId,
            string? search,
            Guid? materialId,
            Guid? locationId,
            string? status,
            DateTimeOffset? expiresBefore,
            int? offset,
            int? limit,
            IQueryHandler<SearchLotsQuery, IReadOnlyList<LotSummaryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SearchLotsQuery(new LotFilter(siteId, search, materialId, locationId, status, expiresBefore, offset ?? 0, limit ?? 500)), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        lots.MapGet("/{id:guid}", async Task<Ok<LotDto>> (
            Guid id,
            IQueryHandler<GetLotQuery, LotDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLotQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.Lot), Permissions.InventoryView);

        lots.MapGet("/{id:guid}/trace", async Task<Ok<IReadOnlyList<LotTraceNodeDto>>> (
            Guid id,
            string? direction,
            IQueryHandler<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new TraceLotQuery(id, !string.Equals(direction, "backward", StringComparison.OrdinalIgnoreCase)), ct)))
        .RequireAny(Site.Of(SiteOf.Lot), Permissions.InventoryView);

        lots.MapPost("/", async Task<Created<LotTransactionResponse>> (
            ReceiveLotRequest request,
            ICommandHandler<ReceiveLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
        {
            var received = await handler.HandleAsync(
                new ReceiveLotCommand(request.MaterialId, request.LocationId, request.LotNumber, request.Quantity, request.UomId,
                    request.SupplierLot, request.ExpiresAt, request.Comment),
                ct);
            return TypedResults.Created($"/api/lots/{received.LotId}", received);
        })
        .RequirePermission(Permissions.LotReceive, Site.FromBody<ReceiveLotRequest>(SiteOf.Equipment, r => r.LocationId));

        lots.MapPost("/{id:guid}/write-up", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            AdjustLotRequest request,
            ICommandHandler<WriteUpLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new WriteUpLotCommand(id, request.Version, request.Quantity ?? 0m, request.UomId ?? Guid.Empty, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.InventoryCorrect, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/write-off", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            AdjustLotRequest request,
            ICommandHandler<WriteOffLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new WriteOffLotCommand(id, request.Version, request.Quantity, request.UomId, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.InventoryCorrect, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/move", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            MoveLotRequest request,
            ICommandHandler<MoveLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new MoveLotCommand(id, request.Version, request.ToLocationId, request.Quantity, request.UomId, request.Comment), ct)))
        .RequirePermission(Permissions.LotMove, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/status", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            ChangeLotStatusRequest request,
            ICommandHandler<ChangeLotStatusCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ChangeLotStatusCommand(id, request.Version, request.Status, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.LotHold, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/consume", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            ConsumeLotRequest request,
            ICommandHandler<ConsumeLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ConsumeLotCommand(id, request.JobId, request.Quantity, request.UomId, request.Comment, request.RunId), ct)))
        .RequirePermission(Permissions.ProductionReport, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/split", async Task<Created<LotTransactionResponse>> (
            Guid id,
            SplitLotRequest request,
            ICommandHandler<SplitLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
        {
            var part = await handler.HandleAsync(
                new SplitLotCommand(id, request.Version, request.Quantity, request.UomId, request.NewLotNumber, request.ToLocationId, request.Comment), ct);
            return TypedResults.Created($"/api/lots/{part.LotId}", part);
        })
        .RequirePermission(Permissions.InventoryCorrect, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/merge", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            MergeLotsRequest request,
            ICommandHandler<MergeLotsCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new MergeLotsCommand(id, request.Version, request.SourceLotIds, request.Comment), ct)))
        .RequirePermission(Permissions.InventoryCorrect, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/ship", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            ShipLotRequest request,
            ICommandHandler<ShipLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ShipLotCommand(id, request.Version, request.Quantity, request.UomId, request.Reference, request.Comment), ct)))
        .RequirePermission(Permissions.LotShip, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/correct", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            CorrectLotRequest request,
            ICommandHandler<CorrectLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new CorrectLotCommand(id, request.Version, request.LotNumber, request.SupplierLot, request.ExpiresAt, request.Comment), ct)))
        .RequirePermission(Permissions.InventoryCorrect, Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/reverse", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            ReverseMovementRequest request,
            ICommandHandler<ReverseMovementCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ReverseMovementCommand(id, request.Version, request.EventId, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.InventoryCorrect, Site.Of(SiteOf.Lot));

        var counts = inventory.MapGroup("/counts");

        counts.MapGet("/", async Task<Ok<IReadOnlyList<StockCountSummaryDto>>> (
            Guid siteId,
            IQueryHandler<ListStockCountsQuery, IReadOnlyList<StockCountSummaryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListStockCountsQuery(siteId), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        counts.MapGet("/{id:guid}", async Task<Ok<StockCountDto>> (
            Guid id,
            IQueryHandler<GetStockCountQuery, StockCountDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetStockCountQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.StockCount), Permissions.InventoryView);

        counts.MapPost("/", async Task<Created<CreatedResponse>> (
            StartStockCountRequest request,
            ICommandHandler<StartStockCountCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new StartStockCountCommand(request.LocationId, request.Note), ct);
            return TypedResults.Created($"/api/inventory/counts/{created.Id}", created);
        })
        .RequirePermission(Permissions.StockCount, Site.FromBody<StartStockCountRequest>(SiteOf.Equipment, r => r.LocationId));

        counts.MapPut("/{id:guid}/lines", async Task<Ok<VersionResponse>> (
            Guid id,
            RecordCountsRequest request,
            ICommandHandler<RecordCountsCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RecordCountsCommand(id, request.Version, request.Lines), ct)))
        .RequirePermission(Permissions.StockCount, Site.Of(SiteOf.StockCount));

        counts.MapPost("/{id:guid}/post", async Task<Ok<PostStockCountResponse>> (
            Guid id,
            PostStockCountRequest request,
            ICommandHandler<PostStockCountCommand, PostStockCountResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new PostStockCountCommand(id, request.Version, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.StockCount, Site.Of(SiteOf.StockCount));

        counts.MapPost("/{id:guid}/cancel", async Task<Ok<VersionResponse>> (
            Guid id,
            CancelStockCountRequest request,
            ICommandHandler<CancelStockCountCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CancelStockCountCommand(id, request.Version), ct)))
        .RequirePermission(Permissions.StockCount, Site.Of(SiteOf.StockCount));

        inventory.MapGet("/reports/stock-on-date", async Task<Ok<IReadOnlyList<StockOnDateDto>>> (
            Guid siteId,
            DateTimeOffset at,
            IQueryHandler<StockOnDateQuery, IReadOnlyList<StockOnDateDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new StockOnDateQuery(siteId, at), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        inventory.MapGet("/reports/aging", async Task<Ok<IReadOnlyList<AgingRowDto>>> (
            Guid siteId,
            IQueryHandler<AgingQuery, IReadOnlyList<AgingRowDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new AgingQuery(siteId), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        var reasons = routes.MapGroup("/reason-codes").WithTags("Reference data");

        reasons.MapGet("/all", async Task<Ok<IReadOnlyList<ReasonCodeAdminDto>>> (
            Guid? siteId,
            HttpContext http,
            IQueryHandler<ListAllReasonCodesQuery, IReadOnlyList<ReasonCodeAdminDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok((await handler.HandleAsync(new ListAllReasonCodesQuery(siteId), ct)).At(await Visible.SitesAsync(http, Permissions.ReasonsView), r => r.SiteId)))
        .RequireAny(null, Permissions.ReasonsView);

        reasons.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateReasonCodeRequest request,
            ICommandHandler<CreateReasonCodeCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateReasonCodeCommand(request.SiteId, request.Kind, request.Code, request.Name, request.RequiresComment, request.SortOrder), ct);
            return TypedResults.Created($"/api/reason-codes/{created.Id}", created);
        })
        .RequirePermission(Permissions.Reasons, Site.FromBody<CreateReasonCodeRequest>(SiteOf.Equipment, r => r.SiteId));

        reasons.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateReasonCodeRequest request,
            ICommandHandler<UpdateReasonCodeCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateReasonCodeCommand(id, request.Version, request.Name, request.RequiresComment, request.SortOrder, request.IsActive), ct)))
        .RequirePermission(Permissions.Reasons, Site.Of(SiteOf.ReasonCode));

        routes.MapGet("/jobs/{id:guid}/materials", async Task<Ok<JobMaterialsDto>> (
            Guid id,
            IQueryHandler<GetJobMaterialsQuery, JobMaterialsDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetJobMaterialsQuery(id), ct)))
            .WithTags("Inventory")
        .RequireAny(Site.Of(SiteOf.Job), Permissions.InventoryView, Permissions.FloorView);

        routes.MapPost("/jobs/{id:guid}/produce", async Task<Created<LotTransactionResponse>> (
            Guid id,
            ProduceLotRequest request,
            ICommandHandler<ProduceLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
        {
            var produced = await handler.HandleAsync(
                new ProduceLotCommand(id, request.LocationId, request.Quantity, request.UomId, request.LotNumber, request.ExpiresAt, request.Comment, request.RunId),
                ct);
            return TypedResults.Created($"/api/lots/{produced.LotId}", produced);
        })
        .RequirePermission(Permissions.ProductionReport, Site.Of(SiteOf.Job))
            .WithTags("Inventory");

        routes.MapGet("/reason-codes", async Task<Ok<IReadOnlyList<ReasonCodeDto>>> (
            string kind,
            Guid? siteId,
            IQueryHandler<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListReasonCodesQuery(kind, siteId), ct)))
            .WithTags("Reference data")
        .RequireAny(Site.Query(), [.. Permissions.Views, .. Permissions.All.Keys]);

        return routes;
    }
}
