using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;

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
                new ListStockQuery(new StockFilter(siteId, search, materialClassId, locationId, inStockOnly ?? false)), ct)));

        inventory.MapGet("/materials/{id:guid}", async Task<Ok<MaterialStockDetailDto>> (
            Guid id,
            Guid? siteId,
            IQueryHandler<GetMaterialStockQuery, MaterialStockDetailDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMaterialStockQuery(id, siteId), ct)));

        inventory.MapGet("/materials/{id:guid}/where-used", async Task<Ok<WhereUsedDto>> (
            Guid id,
            IQueryHandler<WhereUsedQuery, WhereUsedDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new WhereUsedQuery(id), ct)));

        inventory.MapGet("/locations", async Task<Ok<IReadOnlyList<InventoryLocationDto>>> (
            Guid? siteId,
            IQueryHandler<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListInventoryLocationsQuery(siteId), ct)));

        inventory.MapGet("/jobs", async Task<Ok<IReadOnlyList<DeclarableJobDto>>> (
            Guid? siteId,
            string? search,
            Guid? materialId,
            IQueryHandler<ListDeclarableJobsQuery, IReadOnlyList<DeclarableJobDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListDeclarableJobsQuery(siteId, search, materialId), ct)));

        var lots = routes.MapGroup("/lots").WithTags("Inventory");

        lots.MapGet("/", async Task<Ok<IReadOnlyList<LotSummaryDto>>> (
            Guid? siteId,
            string? search,
            Guid? materialId,
            Guid? locationId,
            string? status,
            DateTimeOffset? expiresBefore,
            IQueryHandler<SearchLotsQuery, IReadOnlyList<LotSummaryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SearchLotsQuery(new LotFilter(siteId, search, materialId, locationId, status, expiresBefore)), ct)));

        lots.MapGet("/{id:guid}", async Task<Ok<LotDto>> (
            Guid id,
            IQueryHandler<GetLotQuery, LotDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLotQuery(id), ct)));

        lots.MapGet("/{id:guid}/trace", async Task<Ok<IReadOnlyList<LotTraceNodeDto>>> (
            Guid id,
            string? direction,
            IQueryHandler<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new TraceLotQuery(id, !string.Equals(direction, "backward", StringComparison.OrdinalIgnoreCase)), ct)));

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
        });

        lots.MapPost("/{id:guid}/write-up", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            AdjustLotRequest request,
            ICommandHandler<WriteUpLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new WriteUpLotCommand(id, request.Version, request.Quantity ?? 0m, request.UomId ?? Guid.Empty, request.ReasonCodeId, request.Comment), ct)));

        lots.MapPost("/{id:guid}/write-off", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            AdjustLotRequest request,
            ICommandHandler<WriteOffLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new WriteOffLotCommand(id, request.Version, request.Quantity, request.UomId, request.ReasonCodeId, request.Comment), ct)));

        lots.MapPost("/{id:guid}/move", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            MoveLotRequest request,
            ICommandHandler<MoveLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new MoveLotCommand(id, request.Version, request.ToLocationId, request.Quantity, request.UomId, request.Comment), ct)));

        lots.MapPost("/{id:guid}/status", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            ChangeLotStatusRequest request,
            ICommandHandler<ChangeLotStatusCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ChangeLotStatusCommand(id, request.Version, request.Status, request.ReasonCodeId, request.Comment), ct)));

        lots.MapPost("/{id:guid}/consume", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            ConsumeLotRequest request,
            ICommandHandler<ConsumeLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ConsumeLotCommand(id, request.JobId, request.Quantity, request.UomId, request.Comment), ct)));

        routes.MapGet("/jobs/{id:guid}/materials", async Task<Ok<JobMaterialsDto>> (
            Guid id,
            IQueryHandler<GetJobMaterialsQuery, JobMaterialsDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetJobMaterialsQuery(id), ct)))
            .WithTags("Inventory");

        routes.MapPost("/jobs/{id:guid}/produce", async Task<Created<LotTransactionResponse>> (
            Guid id,
            ProduceLotRequest request,
            ICommandHandler<ProduceLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
        {
            var produced = await handler.HandleAsync(
                new ProduceLotCommand(id, request.LocationId, request.Quantity, request.UomId, request.LotNumber, request.ExpiresAt, request.Comment),
                ct);
            return TypedResults.Created($"/api/lots/{produced.LotId}", produced);
        })
            .WithTags("Inventory");

        routes.MapGet("/reason-codes", async Task<Ok<IReadOnlyList<ReasonCodeDto>>> (
            string kind,
            Guid? siteId,
            IQueryHandler<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListReasonCodesQuery(kind, siteId), ct)))
            .WithTags("Reference data");

        return routes;
    }
}
