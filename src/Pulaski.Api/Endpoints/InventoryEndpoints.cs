using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Api.WorkOrders;
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
            bool? liveTiming,
            IQueryHandler<GetShortagesQuery, ShortageReportDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetShortagesQuery(siteId, equipmentId, horizonDays, liveTiming ?? true), ct)))
        .RequireAnyAtSite(Site.Query(), Permissions.InventoryView);

        // ---- Expected receipts (D-058): supply on its way, for the shortage check ----
        var expected = inventory.MapGroup("/expected-receipts");

        expected.MapGet("/", async Task<Ok<IReadOnlyList<ExpectedReceiptDto>>> (
            Guid siteId,
            bool? includeClosed,
            Guid? materialId,
            IQueryHandler<ListExpectedReceiptsQuery, IReadOnlyList<ExpectedReceiptDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListExpectedReceiptsQuery(siteId, includeClosed ?? false, materialId), ct)))
        .RequireAny(Site.Query(), Permissions.InventoryView);

        expected.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateExpectedReceiptRequest request,
            ICommandHandler<CreateExpectedReceiptCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateExpectedReceiptCommand(request.SiteId, request.MaterialId, request.Quantity, request.UomId, request.ExpectedAt,
                    request.Reference, request.Supplier, request.Note),
                ct);
            return TypedResults.Created($"/api/inventory/expected-receipts/{created.Id}", created);
        })
        .RequirePermission(Permissions.LotReceive, Site.FromBody<CreateExpectedReceiptRequest>(SiteOf.Equipment, r => r.SiteId));

        // One from the ERP, matched on its system and key; safe to send again.
        expected.MapPost("/import", async Task<Ok<ImportExpectedReceiptResponse>> (
            ImportExpectedReceiptRequest request,
            ICommandHandler<ImportExpectedReceiptCommand, ImportExpectedReceiptResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ImportExpectedReceiptCommand(request.SiteId, request.ExternalSystem, request.ExternalKey, request.MaterialCode, request.Quantity,
                    request.UomCode, request.ExpectedAt, request.Reference, request.Supplier, request.Cancelled),
                ct)))
        .RequirePermission(Permissions.LotReceive, Site.FromBody<ImportExpectedReceiptRequest>(SiteOf.Equipment, r => r.SiteId));

        expected.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateExpectedReceiptRequest request,
            ICommandHandler<UpdateExpectedReceiptCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateExpectedReceiptCommand(id, request.Version, request.Quantity, request.UomId, request.ExpectedAt, request.Reference, request.Supplier, request.Note),
                ct)))
        .RequirePermission(Permissions.LotReceive, Site.Of(SiteOf.ExpectedReceipt));

        expected.MapPost("/{id:guid}/cancel", async Task<Ok<VersionResponse>> (
            Guid id,
            CancelExpectedReceiptRequest request,
            ICommandHandler<CancelExpectedReceiptCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CancelExpectedReceiptCommand(id, request.Version), ct)))
        .RequirePermission(Permissions.LotReceive, Site.Of(SiteOf.ExpectedReceipt));

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
            Guid? attributeId,
            string? attributeValue,
            string? attributeMin,
            string? attributeMax,
            IQueryHandler<SearchLotsQuery, IReadOnlyList<LotSummaryDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SearchLotsQuery(
                    new LotFilter(siteId, search, materialId, locationId, status, expiresBefore, offset ?? 0, limit ?? 500),
                    attributeId is { } attribute ? new LotAttributeSearch(attribute, attributeValue, attributeMin, attributeMax) : null),
                ct)))
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
                    request.SupplierLot, request.ExpiresAt, request.Comment, EffectiveAt: request.EffectiveAt,
                    ExpectedReceiptId: request.ExpectedReceiptId),
                ct);
            return TypedResults.Created($"/api/lots/{received.LotId}", received);
        })
        .RequirePermission(Permissions.LotReceive, Site.FromBody<ReceiveLotRequest>(SiteOf.Equipment, r => r.LocationId))
        .RequireToBackdate(Site.FromBody<ReceiveLotRequest>(SiteOf.Equipment, r => r.LocationId));

        lots.MapPost("/{id:guid}/write-up", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            AdjustLotRequest request,
            ICommandHandler<WriteUpLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new WriteUpLotCommand(id, request.Version, request.Quantity ?? 0m, request.UomId ?? Guid.Empty, request.ReasonCodeId, request.Comment, request.EffectiveAt), ct)))
        .RequirePermission(Permissions.InventoryCorrect, Site.Of(SiteOf.Lot))
        .RequireToBackdate(Site.Of(SiteOf.Lot));

        lots.MapPost("/{id:guid}/write-off", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            AdjustLotRequest request,
            ICommandHandler<WriteOffLotCommand, LotTransactionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new WriteOffLotCommand(id, request.Version, request.Quantity, request.UomId, request.ReasonCodeId, request.Comment, request.EffectiveAt), ct)))
        .RequirePermission(Permissions.InventoryCorrect, Site.Of(SiteOf.Lot))
        .RequireToBackdate(Site.Of(SiteOf.Lot));

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
                new ConsumeLotCommand(id, request.JobId, request.Quantity, request.UomId, request.Comment, request.RunId, request.EffectiveAt), ct)))
        .RequirePermission(Permissions.ProductionReport, Site.Of(SiteOf.Lot))
        .RequireToBackdate(Site.Of(SiteOf.Lot));

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

        // ---- Containers, labels and scanning (D-059) ----
        lots.MapPost("/{id:guid}/pack", async Task<Ok<LotTransactionResponse>> (
            Guid id,
            PackLotRequest request,
            ICommandHandler<PackLotCommand, LotTransactionResponse> pack,
            ICommandHandler<UnpackLotCommand, LotTransactionResponse> unpack,
            CancellationToken ct) =>
            TypedResults.Ok(request.ContainerId is { } containerId
                ? await pack.HandleAsync(new PackLotCommand(id, request.Version, containerId, request.Comment), ct)
                : await unpack.HandleAsync(new UnpackLotCommand(id, request.Version, request.Comment), ct)))
        .RequirePermission(Permissions.LotMove, Site.Of(SiteOf.Lot));

        lots.MapGet("/{id:guid}/label.pdf", async Task<FileContentHttpResult> (
            Guid id,
            IQueryHandler<GetLotQuery, LotDto> handler,
            LabelPdfRenderer renderer,
            CancellationToken ct) =>
        {
            var detail = await handler.HandleAsync(new GetLotQuery(id), ct);
            var lot = detail.Lot;
            var title = InventoryLabels.LotTitle(lot);
            return TypedResults.File(renderer.Render([InventoryLabels.ForLot(lot, detail.SiteTimeZone)]), "application/pdf", $"label-{InventoryLabels.FileName(title)}.pdf");
        })
        .RequireAny(Site.Of(SiteOf.Lot), Permissions.InventoryView);

        var containers = inventory.MapGroup("/containers");

        containers.MapGet("/", async Task<Ok<IReadOnlyList<ContainerDto>>> (
            Guid siteId,
            string? search,
            bool? includeRetired,
            IQueryHandler<ListContainersQuery, IReadOnlyList<ContainerDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListContainersQuery(siteId, search, includeRetired ?? false), ct)))
        .RequireAny(Site.Query(), Permissions.InventoryView);

        containers.MapGet("/{id:guid}", async Task<Ok<ContainerDto>> (
            Guid id,
            IQueryHandler<GetContainerQuery, ContainerDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetContainerQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.Container), Permissions.InventoryView);

        containers.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateContainerRequest request,
            ICommandHandler<CreateContainerCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateContainerCommand(request.SiteId, request.Code, request.Kind, request.LocationId), ct);
            return TypedResults.Created($"/api/inventory/containers/{created.Id}", created);
        })
        .RequirePermission(Permissions.LotMove, Site.FromBody<CreateContainerRequest>(SiteOf.Equipment, r => r.SiteId));

        containers.MapPost("/{id:guid}/move", async Task<Ok<VersionResponse>> (
            Guid id,
            MoveContainerRequest request,
            ICommandHandler<MoveContainerCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new MoveContainerCommand(id, request.Version, request.ToLocationId, request.Comment), ct)))
        .RequirePermission(Permissions.LotMove, Site.Of(SiteOf.Container));

        containers.MapPost("/{id:guid}/retire", async Task<Ok<VersionResponse>> (
            Guid id,
            RetireContainerRequest request,
            ICommandHandler<RetireContainerCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RetireContainerCommand(id, request.Version), ct)))
        .RequirePermission(Permissions.LotMove, Site.Of(SiteOf.Container));

        containers.MapGet("/{id:guid}/label.pdf", async Task<FileContentHttpResult> (
            Guid id,
            IQueryHandler<GetContainerQuery, ContainerDto> handler,
            LabelPdfRenderer renderer,
            CancellationToken ct) =>
        {
            var container = await handler.HandleAsync(new GetContainerQuery(id), ct);
            return TypedResults.File(renderer.Render([InventoryLabels.ForContainer(container)]), "application/pdf", $"label-{InventoryLabels.FileName(container.Code)}.pdf");
        })
        .RequireAny(Site.Of(SiteOf.Container), Permissions.InventoryView);

        inventory.MapGet("/locations/{id:guid}/label.pdf", async Task<FileContentHttpResult> (
            Guid id,
            Guid siteId,
            IQueryHandler<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>> handler,
            LabelPdfRenderer renderer,
            CancellationToken ct) =>
        {
            var location = (await handler.HandleAsync(new ListInventoryLocationsQuery(siteId), ct)).FirstOrDefault(l => l.Id == id)
                ?? throw new Application.Errors.NotFoundException($"Location {id} not found at that site.");
            return TypedResults.File(renderer.Render([InventoryLabels.ForLocation(location)]), "application/pdf", $"label-{InventoryLabels.FileName(location.Code)}.pdf");
        })
        .RequireAny(Site.Query(), Permissions.InventoryView);

        // How a balance at a location flows, for genealogy through it (D-060); an equipment setting.
        inventory.MapPut("/locations/{id:guid}/flow", async Task<Ok<VersionResponse>> (
            Guid id,
            SetFlowRequest request,
            ICommandHandler<SetFlowCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetFlowCommand(id, request.Version, request.FlowModel, request.MinResidenceMinutes), ct)))
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.Equipment));

        // Where a balance's withdrawals came from, by its location's flow model (D-060).
        lots.MapGet("/{id:guid}/allocation", async Task<Ok<BalanceAllocationDto>> (
            Guid id,
            IQueryHandler<GetBalanceAllocationQuery, BalanceAllocationDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetBalanceAllocationQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.Lot), Permissions.InventoryView);

        // What a scanned code names at the site: one lot, container or location, a choice of several, or nothing (D-059).
        inventory.MapGet("/scan", async Task<Ok<ScanResultDto>> (
            Guid siteId,
            string code,
            IQueryHandler<ResolveScanQuery, ScanResultDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ResolveScanQuery(siteId, code), ct)))
        .RequireAny(Site.Query(), Permissions.InventoryView);

        // ---- Lot attributes and documents (D-056) ----
        lots.MapGet("/{id:guid}/records", async Task<Ok<LotRecordsDto>> (
            Guid id,
            IQueryHandler<GetLotRecordsQuery, LotRecordsDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLotRecordsQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.Lot), Permissions.InventoryView);

        lots.MapPut("/{id:guid}/attributes/{attributeId:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            Guid attributeId,
            SetLotAttributeRequest request,
            ICommandHandler<SetLotAttributeCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetLotAttributeCommand(id, attributeId, request.Version, request.Value), ct)))
        .RequirePermission(Permissions.LotRecords, Site.Of(SiteOf.Lot));

        // A file (PDF, PNG or JPEG, at most 10 MB), a reference (document number or URL), or both; as multipart form fields.
        lots.MapPost("/{id:guid}/documents", async Task<Created<CreatedResponse>> (
            Guid id,
            HttpRequest http,
            ICommandHandler<AttachLotDocumentCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var form = await http.ReadFormAsync(ct);
            byte[]? content = null;
            string? fileName = null;
            if (form.Files.Count > 0)
            {
                var file = form.Files[0];
                if (file.Length > LotDocumentFormat.MaxBytes)
                {
                    throw new Application.Errors.ValidationException(new Dictionary<string, string[]>
                    {
                        ["file"] = [$"The file is larger than {LotDocumentFormat.MaxBytes / 1024 / 1024} MB."],
                    });
                }

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                content = buffer.ToArray();
                fileName = file.FileName;
            }

            string? Field(string name) => form.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : null;
            DateOnly? issuedOn = Field("issuedOn") is { } day
                ? DateOnly.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                    ? d
                    : throw new Application.Errors.ValidationException(new Dictionary<string, string[]> { ["issuedOn"] = ["A date (yyyy-MM-dd)."] })
                : null;
            var created = await handler.HandleAsync(
                new AttachLotDocumentCommand(id, Field("kind") ?? "", Field("title") ?? "", Field("documentNumber"), Field("issuer"), issuedOn,
                    Field("url"), fileName, content),
                ct);
            return TypedResults.Created($"/api/lot-documents/{created.Id}", created);
        })
        .RequirePermission(Permissions.LotRecords, Site.Of(SiteOf.Lot))
        .DisableAntiforgery()
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(LotDocumentFormat.MaxBytes + (1024 * 1024)))
        .Accepts<IFormFile>("multipart/form-data");

        var documents = routes.MapGroup("/lot-documents").WithTags("Inventory");

        // The stored file, as an attachment and never sniffed by the browser.
        documents.MapGet("/{id:guid}/content", async (
            Guid id,
            HttpResponse response,
            IQueryHandler<GetLotDocumentContentQuery, LotDocumentContent> handler,
            CancellationToken ct) =>
        {
            var file = await handler.HandleAsync(new GetLotDocumentContentQuery(id), ct);
            response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.File(file.Content, file.ContentType, file.FileName);
        })
        .RequireAny(Site.Of(SiteOf.LotDocument), Permissions.InventoryView);

        documents.MapPost("/{id:guid}/withdraw", async Task<Ok<VersionResponse>> (
            Guid id,
            WithdrawLotDocumentRequest request,
            ICommandHandler<WithdrawLotDocumentCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new WithdrawLotDocumentCommand(id, request.Version, request.Reason), ct)))
        .RequirePermission(Permissions.LotRecords, Site.Of(SiteOf.LotDocument));

        var attributes = routes.MapGroup("/lot-attributes").WithTags("Inventory");

        attributes.MapGet("/", async Task<Ok<IReadOnlyList<LotAttributeDto>>> (
            Guid siteId,
            IQueryHandler<ListLotAttributesQuery, IReadOnlyList<LotAttributeDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListLotAttributesQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.InventoryView, Permissions.LotAttributes);

        attributes.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateLotAttributeRequest request,
            ICommandHandler<CreateLotAttributeCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateLotAttributeCommand(request.SiteId, request.Code, request.Name, request.DataType, request.UomId, request.Choices,
                    request.AppliesToAll, request.ClassIds, request.SortOrder),
                ct);
            return TypedResults.Created($"/api/lot-attributes/{created.Id}", created);
        })
        .RequirePermission(Permissions.LotAttributes, Site.FromBody<CreateLotAttributeRequest>(SiteOf.Equipment, r => r.SiteId));

        attributes.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateLotAttributeRequest request,
            ICommandHandler<UpdateLotAttributeCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateLotAttributeCommand(id, request.Version, request.Name, request.UomId, request.Choices, request.AppliesToAll, request.ClassIds,
                    request.SortOrder, request.IsActive),
                ct)))
        .RequirePermission(Permissions.LotAttributes, Site.Of(SiteOf.LotAttribute));

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
                new CreateReasonCodeCommand(request.SiteId, request.Kind, request.Code, request.Name, request.RequiresComment, request.SortOrder, request.UtilizationClass,
                    request.IsGroup, request.ParentId), ct);
            return TypedResults.Created($"/api/reason-codes/{created.Id}", created);
        })
        .RequirePermission(Permissions.Reasons, Site.FromBody<CreateReasonCodeRequest>(SiteOf.Equipment, r => r.SiteId));

        reasons.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id,
            UpdateReasonCodeRequest request,
            ICommandHandler<UpdateReasonCodeCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateReasonCodeCommand(id, request.Version, request.Name, request.RequiresComment, request.SortOrder, request.IsActive, request.UtilizationClass), ct)))
        .RequirePermission(Permissions.Reasons, Site.Of(SiteOf.ReasonCode));

        reasons.MapPut("/{id:guid}/parent", async Task<Ok<VersionResponse>> (
            Guid id,
            MoveReasonCodeRequest request,
            ICommandHandler<MoveReasonCodeCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new MoveReasonCodeCommand(id, request.Version, request.ParentId), ct)))
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
                new ProduceLotCommand(id, request.LocationId, request.Quantity, request.UomId, request.LotNumber, request.ExpiresAt, request.Comment, request.RunId,
                    request.EffectiveAt),
                ct);
            return TypedResults.Created($"/api/lots/{produced.LotId}", produced);
        })
        .RequirePermission(Permissions.ProductionReport, Site.Of(SiteOf.Job))
        .RequireToBackdate(Site.Of(SiteOf.Job))
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
