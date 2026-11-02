using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Api.WorkOrders;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>A site's label setup, and printing labels to its printers (#75, D-080).</summary>
internal static class LabelEndpoints
{
    public static IEndpointRouteBuilder MapLabelEndpoints(this IEndpointRouteBuilder routes)
    {
        var labels = routes.MapGroup("/labels").WithTags("Labels");

        labels.MapGet("/sites/{siteId:guid}", async Task<Ok<LabelSetupDto>> (Guid siteId, IQueryHandler<GetLabelSetupQuery, LabelSetupDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLabelSetupQuery(siteId), ct)))
        .RequireAny(Site.Route(), Permissions.SiteSettingsView, Permissions.InventoryView, Permissions.FloorView);

        labels.MapPut("/sites/{siteId:guid}", async Task<Ok<VersionResponse>> (
            Guid siteId, SetLabelSettingsRequest request, ICommandHandler<SetLabelSettingsCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SetLabelSettingsCommand(siteId, request.Version, request.LabelFormat, request.Gs1CompanyPrefix, request.SsccExtension, request.DefaultPrinterId), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        labels.MapPost("/sites/{siteId:guid}/printers", async Task<Ok<CreatedResponse>> (
            Guid siteId, SaveLabelPrinterRequest r, ICommandHandler<SaveLabelPrinterCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SaveLabelPrinterCommand(siteId, null, 0, r.Code, r.Name, r.Method, r.Host, r.Port, r.DropFolder, r.TemplateSet, r.IsActive), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        labels.MapPut("/printers/{id:guid}", async Task<Ok<CreatedResponse>> (
            Guid id, SaveLabelPrinterRequest r, ILabelReadStore store, ICommandHandler<SaveLabelPrinterCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var printer = await store.GetPrinterAsync(id, ct) ?? throw new NotFoundException($"Printer {id} not found.");
            return TypedResults.Ok(await handler.HandleAsync(
                new SaveLabelPrinterCommand(printer.SiteId, id, r.Version, printer.Code, r.Name, r.Method, r.Host, r.Port, r.DropFolder, r.TemplateSet, r.IsActive), ct));
        })
        .RequirePermission(Permissions.SiteSettings, Site.Of(SiteOf.LabelPrinter));

        labels.MapPost("/sites/{siteId:guid}/supplier-formats", async Task<Ok<CreatedResponse>> (
            Guid siteId, SaveSupplierFormatRequest r, ICommandHandler<SaveSupplierFormatCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveSupplierFormatCommand(siteId, null, 0, r.Code, r.Name, r.Pattern, r.ExpiryFormat, r.SortOrder, r.IsActive), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        labels.MapPut("/supplier-formats/{id:guid}", async Task<Ok<CreatedResponse>> (
            Guid id, Guid siteId, SaveSupplierFormatRequest r, ICommandHandler<SaveSupplierFormatCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveSupplierFormatCommand(siteId, id, r.Version, r.Code, r.Name, r.Pattern, r.ExpiryFormat, r.SortOrder, r.IsActive), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Of(SiteOf.SupplierBarcodeFormat));

        labels.MapPost("/supplier-formats/test", async Task<Ok<SupplierFormatTestDto>> (
            TestSupplierFormatRequest r, IQueryHandler<TestSupplierFormatQuery, SupplierFormatTestDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new TestSupplierFormatQuery(r.Pattern, r.ExpiryFormat, r.Sample), ct)))
        .RequireAnyView();

        // The template sets and drop folders this API is configured with.
        labels.MapGet("/options", Ok<LabelOptionsDto> (LabelTemplates templates) =>
            TypedResults.Ok(new LabelOptionsDto { TemplateSets = templates.Sets(), DropFolders = templates.DropFolders() }))
        .RequireAnyView();

        labels.MapPost("/print", async Task<Ok<PrintLabelsResponse>> (
            PrintLabelsRequest request, LabelBuilder builder, LabelPrinting printing, ILabelReadStore store, CancellationToken ct) =>
        {
            var problems = new Dictionary<string, string[]>();
            if (request.Kind is not (LabelBuilder.Lot or LabelBuilder.Container or LabelBuilder.Location))
            {
                problems["Kind"] = ["lot, container or location."];
            }

            if (request.Ids.Count is < 1 or > LabelPrinting.MaxLabels)
            {
                problems["Ids"] = [$"1 to {LabelPrinting.MaxLabels} labels."];
            }

            if (request.Copies is < 1 or > 20)
            {
                problems["Copies"] = ["1 to 20 copies."];
            }

            if (problems.Count > 0)
            {
                throw new ValidationException(problems);
            }

            var setup = await store.GetSetupAsync(request.SiteId, ct);
            var printerId = request.PrinterId ?? setup.DefaultPrinterId
                ?? throw new Domain.DomainRuleException("Choose a printer: the site has no default printer.");
            var printer = setup.Printers.FirstOrDefault(p => p.Id == printerId) ?? throw new NotFoundException("That printer isn't one of this site's.");
            var content = await builder.BuildAsync(request.SiteId, request.Kind, request.Ids, setup.LabelFormat, ct);
            return TypedResults.Ok(await printing.PrintAsync(printer, request.Kind, content, request.Copies, ct));
        })
        .RequireAny(Site.FromBody<PrintLabelsRequest>(SiteOf.Equipment, r => r.SiteId), Permissions.InventoryView, Permissions.FloorView);

        return routes;
    }
}

/// <summary>Builds lot, container and location labels in a site's format, for PDFs and printers (#75).</summary>
internal sealed class LabelBuilder(
    IQueryHandler<GetLotQuery, LotDto> lots,
    IQueryHandler<GetContainerQuery, ContainerDto> containers,
    IQueryHandler<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>> locations,
    ILabelReadStore labels)
{
    public const string Lot = "lot";
    public const string Container = "container";
    public const string Location = "location";

    public async Task<string> FormatAsync(Guid siteId, CancellationToken cancellationToken) => (await labels.GetSetupAsync(siteId, cancellationToken)).LabelFormat;

    /// <summary>The labels, in the order asked; each must be at the site.</summary>
    public async Task<IReadOnlyList<LabelContent>> BuildAsync(Guid siteId, string kind, IReadOnlyList<Guid> ids, string format, CancellationToken cancellationToken)
    {
        var result = new List<LabelContent>();
        var siteLocations = kind == Location ? await locations.HandleAsync(new ListInventoryLocationsQuery(siteId), cancellationToken) : [];
        foreach (var id in ids)
        {
            switch (kind)
            {
                case Lot:
                    var lot = await lots.HandleAsync(new GetLotQuery(id), cancellationToken);
                    result.Add(lot.Lot.SiteId == siteId ? InventoryLabels.ForLot(lot.Lot, lot.SiteTimeZone, format) : throw new NotFoundException($"Lot {id} isn't at this site."));
                    break;
                case Container:
                    var container = await containers.HandleAsync(new GetContainerQuery(id), cancellationToken);
                    result.Add(container.SiteId == siteId ? InventoryLabels.ForContainer(container, format) : throw new NotFoundException($"Container {id} isn't at this site."));
                    break;
                default:
                    result.Add(InventoryLabels.ForLocation(siteLocations.FirstOrDefault(l => l.Id == id) ?? throw new NotFoundException($"Location {id} isn't at this site.")));
                    break;
            }
        }

        return result;
    }
}
