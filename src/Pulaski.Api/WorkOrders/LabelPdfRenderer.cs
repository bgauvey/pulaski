using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Pulaski.Domain.Inventory;

namespace Pulaski.Api.WorkOrders;

/// <summary>
/// What one label shows (D-059): a title, its Pulaski barcode, and a few lines of text; in a GS1 format (#75) also the GS1 elements its
/// GS1 barcode carries. <see cref="Fields"/> are the named values a label template can use.
/// </summary>
public sealed record LabelContent(
    string Kind, string Title, string Barcode, IReadOnlyList<string> Lines, string Format = "pulaski", IReadOnlyList<Gs1Element>? Gs1 = null)
{
    public IReadOnlyDictionary<string, string?> Fields { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// Lot, container and location labels (D-059): one 4 × 6 in page each, with a Code 128 barcode of the label's code and the same code
/// in text beneath it, so a person can type it when the barcode won't scan. A GS1 label (#75) leads with its GS1-128 or GS1 DataMatrix and
/// the elements in brackets beneath, and keeps the Pulaski code small at the foot. Any printer that prints PDF prints them.
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
            double y;
            if (label.Gs1 is { } gs1)
            {
                var hri = Gs1.HumanReadable(gs1);
                if (label.Format == "gs1_datamatrix")
                {
                    BarcodeDrawing.Gs1DataMatrix(graphics, gs1, Margin, 92, 100);
                    graphics.DrawString(hri, CodeFont, XBrushes.Black, new XRect(Margin + 112, 92, inner - 112, 100), XStringFormats.TopLeft);
                    y = 206;
                }
                else
                {
                    BarcodeDrawing.Gs1128(graphics, gs1, Margin, 92, inner, 80);
                    graphics.DrawString(hri, CodeFont, XBrushes.Black, new XRect(Margin, 176, inner, 16), XStringFormats.TopCenter);
                    y = 204;
                }

                // Pulaski's own code, small, so a scan in Pulaski still names exactly this one.
                BarcodeDrawing.Code128(graphics, label.Barcode, Margin, Height - Margin - 46, inner * 0.6, 30);
                graphics.DrawString(label.Barcode, CodeFont, XBrushes.Black, new XRect(Margin, Height - Margin - 14, inner * 0.6, 14), XStringFormats.TopCenter);
            }
            else
            {
                BarcodeDrawing.Code128(graphics, label.Barcode, Margin, 92, inner, 90);
                graphics.DrawString(label.Barcode, CodeFont, XBrushes.Black, new XRect(Margin, 186, inner, 16), XStringFormats.TopCenter);
                y = 220;
            }

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
