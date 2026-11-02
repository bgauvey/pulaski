using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Api.WorkOrders;

/// <summary>
/// A printable picking list (#82): one row per allocation still to fetch, in the order the list comes (by location, then container),
/// with a box to tick; landscape Letter, as many pages as it takes.
/// </summary>
public sealed class PickingListPdfRenderer
{
    private const double PageWidth = 792;
    private const double PageHeight = 612;
    private const double Margin = 36;
    private const double RowHeight = 20;
    private static readonly XColor InkColor = XColor.FromArgb(35, 52, 69);
    private static readonly XColor MutedColor = XColor.FromArgb(93, 106, 119);
    private static readonly XColor RuleColor = XColor.FromArgb(202, 210, 216);
    private static readonly XColor AccentColor = XColor.FromArgb(20, 83, 105);
    private static readonly XFont TitleFont;
    private static readonly XFont LabelFont;
    private static readonly XFont BodyFont;
    private static readonly XFont SmallFont;

    // Column x positions and widths: tick, location, container, lot, material, quantity, job and line, needed from.
    private static readonly (string Title, double X, double Width)[] Columns =
    [
        ("", Margin, 18),
        ("LOCATION", Margin + 22, 84),
        ("CONTAINER", Margin + 110, 74),
        ("LOT", Margin + 188, 110),
        ("MATERIAL", Margin + 302, 170),
        ("QUANTITY", Margin + 476, 76),
        ("FOR", Margin + 560, 92),
        ("NEEDED FROM", Margin + 656, 64),
    ];

    static PickingListPdfRenderer()
    {
        GlobalFontSettings.FontResolver ??= new ShopOrderPdfRenderer.RobotoFontResolver();
        TitleFont = new XFont("Roboto", 16, XFontStyleEx.Bold);
        LabelFont = new XFont("Roboto", 7, XFontStyleEx.Bold);
        BodyFont = new XFont("Roboto", 9, XFontStyleEx.Regular);
        SmallFont = new XFont("Roboto", 7, XFontStyleEx.Regular);
    }

    public byte[] Render(PickingListDto list, DateTimeOffset printedAt)
    {
        using var document = new PdfDocument();
        document.Info.Title = $"Picking list {list.SiteCode}{(list.Scope is null ? "" : $" {list.Scope}")}";
        document.Info.Creator = "Pulaski";
        var zone = Zone(list.SiteTimeZone);
        var pageNumber = 0;
        var y = 0d;
        XGraphics? graphics = null;
        void NewPage()
        {
            graphics?.Dispose();
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.Letter;
            page.Orientation = PdfSharp.PageOrientation.Landscape;
            graphics = XGraphics.FromPdfPage(page);
            pageNumber++;
            y = Header(graphics, list, printedAt, zone, pageNumber);
        }

        NewPage();
        if (list.Lines.Count == 0)
        {
            Text(graphics!, "Nothing to pick: no open job has allocated stock away from its line.", Margin, y + 4, PageWidth - 2 * Margin, BodyFont, InkColor);
        }

        foreach (var line in list.Lines)
        {
            if (y + RowHeight > PageHeight - Margin)
            {
                NewPage();
            }

            Row(graphics!, line, y, zone);
            y += RowHeight;
        }

        graphics?.Dispose();
        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static double Header(XGraphics graphics, PickingListDto list, DateTimeOffset printedAt, TimeZoneInfo zone, int pageNumber)
    {
        Text(graphics, "PICKING LIST", Margin, 24, 300, TitleFont, AccentColor);
        var scope = list.Scope is null ? "every open job" : list.Scope;
        Text(graphics, $"{list.SiteCode} · {scope} · printed {Local(printedAt, zone)} ({list.SiteTimeZone}) · page {pageNumber}",
            Margin, 46, PageWidth - 2 * Margin, SmallFont, MutedColor);
        var top = 66d;
        foreach (var (title, x, width) in Columns)
        {
            Text(graphics, title, x, top, width, LabelFont, MutedColor);
        }

        Line(graphics, top + 13);
        return top + 18;
    }

    private static void Row(XGraphics graphics, PickingLineDto line, double y, TimeZoneInfo zone)
    {
        graphics.DrawRectangle(new XPen(InkColor, 0.8), Columns[0].X + 2, y + 2, 10, 10);
        var material = line.MaterialCode == line.ComponentCode ? $"{line.MaterialCode} · {line.MaterialName}" : $"{line.MaterialCode} (for {line.ComponentCode})";
        string[] values =
        [
            "",
            line.LocationCode ?? "—",
            line.ContainerCode ?? "",
            line.LotLabel,
            material,
            $"{line.Quantity.ToString("#,##0.###", CultureInfo.InvariantCulture)} {line.UomCode}",
            $"{line.JobNumber} · {line.LineCode}",
            line.NeededFrom is { } at ? Local(at, zone) : "—",
        ];
        for (var i = 1; i < Columns.Length; i++)
        {
            Fitted(graphics, values[i], Columns[i].X, y + 1, Columns[i].Width);
        }

        Line(graphics, y + RowHeight - 3);
    }

    private static void Fitted(XGraphics graphics, string value, double x, double y, double width)
    {
        var font = BodyFont;
        while (font.Size > 6 && graphics.MeasureString(value, font).Width > width)
        {
            font = new XFont("Roboto", font.Size - 0.5, font.Style);
        }

        Text(graphics, value, x, y, width, font, InkColor);
    }

    private static void Text(XGraphics graphics, string value, double x, double y, double width, XFont font, XColor color) =>
        graphics.DrawString(value, font, new XSolidBrush(color), new XRect(x, y, width, font.Size + 6), XStringFormats.TopLeft);

    private static void Line(XGraphics graphics, double y) => graphics.DrawLine(new XPen(RuleColor, 0.6), Margin, y, PageWidth - Margin, y);

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    private static string Local(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
