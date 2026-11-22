using Pulaski.Api.Endpoints;
using Pulaski.Application.Errors;
using Pulaski.Application.Execution;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;

namespace Pulaski.Api.WorkOrders;

/// <summary>
/// Prints the labels of lots a line closed (#122, D-111) on the line's label printer, as BarTender's <c>lot.produced</c> operation (D-110).
/// Never throws for a printing problem: the lots are made either way, so what happened comes back as a note.
/// </summary>
internal sealed partial class LineLabels(ILabelReadStore labels, LabelBuilder builder, LabelPrinting printing, ILogger<LineLabels> logger) : ILineLabels
{
    public const string Operation = "lot.produced";

    public async Task<IReadOnlyList<InventoryWarningDto>> PrintClosedLotsAsync(Guid lineId, IReadOnlyList<Guid> lotIds, CancellationToken cancellationToken)
    {
        if (lotIds.Count == 0 || await labels.GetLinePrinterAsync(lineId, cancellationToken) is not (_, { IsActive: true } printer))
        {
            return [];
        }

        IReadOnlyList<LabelContent> content = [];
        try
        {
            var format = await builder.FormatAsync(printer.SiteId, cancellationToken);
            content = await builder.BuildAsync(printer.SiteId, LabelBuilder.Lot, lotIds, format, cancellationToken);
            await printing.PrintAsync(printer, LabelBuilder.Lot, content, 1, cancellationToken, Operation);
            return [new InventoryWarningDto("label_printed", $"{Labels(content)} printed on {printer.Code}.")];
        }
        catch (Exception ex) when (ex is DomainRuleException or NotFoundException or IOException or HttpRequestException)
        {
            LogNotPrinted(logger, ex, printer.Code);
            return [new InventoryWarningDto("label_not_printed", $"{(content.Count == 0 ? "The lots' labels" : Labels(content))} didn't print on {printer.Code}: {ex.Message} Print them from the lot.")];
        }
    }

    private static string Labels(IReadOnlyList<LabelContent> content) =>
        content.Count == 1 ? $"Lot {content[0].Title}'s label" : $"The labels of lots {string.Join(", ", content.Select(c => c.Title))}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "A lot label didn't print on {Printer}.")]
    private static partial void LogNotPrinted(ILogger logger, Exception exception, string printer);
}
