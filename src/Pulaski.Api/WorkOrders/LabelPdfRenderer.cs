using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Pulaski.Api.WorkOrders;

/// <summary>What one label shows (D-059): a title, its barcode, and a few lines of text.</summary>
public sealed record LabelContent(string Kind, string Title, string Barcode, IReadOnlyList<string> Lines);

/// <summary>
/// Lot, container and location labels (D-059): one 4 × 6 in page each, with a Code 128 barcode of the label's code and the same code
/// in text beneath it, so a person can type it when the barcode won't scan. Any printer that prints PDF prints them.
/// </summary>
public sealed class LabelPdfRenderer
{
    private const double Width = 288;   // 4 in
    private const double Height = 432;  // 6 in
    private const double Margin = 18;
    private static readonly XFont KindFont;
    private static readonly XFont TitleFont;
    private static readonly XFont CodeFont;
    private static readonly XFont LineFont;

    static LabelPdfRenderer()
    {
        GlobalFontSettings.FontResolver ??= new ShopOrderPdfRenderer.RobotoFontResolver();
        KindFont = new XFont("Roboto", 10, XFontStyleEx.Bold);
        TitleFont = new XFont("Roboto", 26, XFontStyleEx.Bold);
        CodeFont = new XFont("Roboto", 11, XFontStyleEx.Regular);
        LineFont = new XFont("Roboto", 13, XFontStyleEx.Regular);
    }

    public byte[] Render(IEnumerable<LabelContent> labels)
    {
        using var document = new PdfDocument();
        foreach (var label in labels)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(Width);
            page.Height = XUnit.FromPoint(Height);
            using var graphics = XGraphics.FromPdfPage(page);
            var inner = Width - 2 * Margin;

            graphics.DrawString(label.Kind.ToUpperInvariant(), KindFont, XBrushes.Gray, new XRect(Margin, Margin, inner, 14), XStringFormats.TopLeft);
            var title = TitleFont;
            while (title.Size > 12 && graphics.MeasureString(label.Title, title).Width > inner)
            {
                title = new XFont("Roboto", title.Size - 1, XFontStyleEx.Bold);
            }

            graphics.DrawString(label.Title, title, XBrushes.Black, new XRect(Margin, Margin + 16, inner, title.Size + 8), XStringFormats.TopLeft);
            BarcodeDrawing.Code128(graphics, label.Barcode, Margin, 92, inner, 90);
            graphics.DrawString(label.Barcode, CodeFont, XBrushes.Black, new XRect(Margin, 186, inner, 16), XStringFormats.TopCenter);

            var y = 220.0;
            foreach (var line in label.Lines.Take(10))
            {
                graphics.DrawString(line, LineFont, XBrushes.Black, new XRect(Margin, y, inner, 18), XStringFormats.TopLeft);
                y += 20;
            }
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
