using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Pulaski.Application.Quality;
using Pulaski.Contracts.Quality;

namespace Pulaski.Api.WorkOrders;

/// <summary>
/// A lot's certificate of analysis (#137, D-123): portrait Letter, the issuing site, the lot and its specification version, one row per spec item
/// with its requirement, results and outcome, and every warning printed plainly above the signature line. In English, as Pulaski's other prints.
/// </summary>
public sealed class CertificatePdfRenderer : ICertificateRenderer
{
    private const double PageWidth = 612;
    private const double PageHeight = 792;
    private const double Margin = 48;
    private const double RowHeight = 22;
    private static readonly XColor InkColor = XColor.FromArgb(35, 52, 69);
    private static readonly XColor MutedColor = XColor.FromArgb(93, 106, 119);
    private static readonly XColor RuleColor = XColor.FromArgb(202, 210, 216);
    private static readonly XColor AccentColor = XColor.FromArgb(20, 83, 105);
    private static readonly XColor FailColor = XColor.FromArgb(176, 32, 32);
    private static readonly XFont TitleFont;
    private static readonly XFont LabelFont;
    private static readonly XFont BodyFont;
    private static readonly XFont StrongFont;
    private static readonly XFont SmallFont;

    // Characteristic, requirement, results, outcome.
    private static readonly (string Title, double X, double Width)[] Columns =
    [
        ("CHARACTERISTIC", Margin, 168),
        ("REQUIREMENT", Margin + 172, 110),
        ("RESULT", Margin + 286, 160),
        ("OUTCOME", Margin + 450, 66),
    ];

    static CertificatePdfRenderer()
    {
        GlobalFontSettings.FontResolver ??= new ShopOrderPdfRenderer.RobotoFontResolver();
        TitleFont = new XFont("Roboto", 18, XFontStyleEx.Bold);
        LabelFont = new XFont("Roboto", 7, XFontStyleEx.Bold);
        BodyFont = new XFont("Roboto", 9, XFontStyleEx.Regular);
        StrongFont = new XFont("Roboto", 9, XFontStyleEx.Bold);
        SmallFont = new XFont("Roboto", 7.5, XFontStyleEx.Regular);
    }

    public byte[] Render(CertificateDto certificate)
    {
        var c = certificate;
        using var document = new PdfDocument();
        var label = c.LotNumber ?? c.MaterialCode;
        document.Info.Title = $"Certificate of analysis {label}";
        document.Info.Creator = "Pulaski";
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(c.SiteTimeZone ?? "UTC", out var z) ? z : TimeZoneInfo.Utc;
        XGraphics? graphics = null;
        var y = 0d;
        var page = 0;
        void NewPage()
        {
            graphics?.Dispose();
            var p = document.AddPage();
            p.Size = PdfSharp.PageSize.Letter;
            graphics = XGraphics.FromPdfPage(p);
            page++;
            y = page == 1 ? Header(graphics, c, zone) : Margin;
            y = Columns_(graphics, y);
        }

        NewPage();
        foreach (var line in c.Lines)
        {
            if (y + RowHeight > PageHeight - Margin - 110)
            {
                NewPage();
            }

            Row(graphics!, line, y);
            y += RowHeight;
        }

        y += 12;
        if (c.Warnings.Count > 0)
        {
            Text(graphics!, "NOTES", Margin, y, 200, LabelFont, FailColor);
            y += 12;
            foreach (var warning in c.Warnings)
            {
                Text(graphics!, "• " + warning.Text, Margin, y, PageWidth - 2 * Margin, BodyFont, InkColor);
                y += 13;
            }
        }
        else
        {
            Text(graphics!, "Every characteristic of the specification conforms.", Margin, y, PageWidth - 2 * Margin, StrongFont, InkColor);
            y += 13;
        }

        y = Math.Max(y + 30, PageHeight - Margin - 50);
        graphics!.DrawLine(new XPen(InkColor, 0.6), Margin, y, Margin + 220, y);
        Text(graphics, $"Issued by {c.SiteName} on {Local(c.IssuedAt, zone)}", Margin, y + 4, 300, SmallFont, MutedColor);
        Text(graphics, $"Results as recorded in Pulaski; {page} page{(page == 1 ? "" : "s")}.", Margin, y + 15, 300, SmallFont, MutedColor);
        graphics.Dispose();
        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static double Header(XGraphics g, CertificateDto c, TimeZoneInfo zone)
    {
        Text(g, "CERTIFICATE OF ANALYSIS", Margin, Margin - 8, 400, TitleFont, AccentColor);
        Text(g, $"{c.SiteName} ({c.SiteCode})", Margin, Margin + 18, 400, BodyFont, MutedColor);
        var facts = new List<(string, string)>
        {
            ("LOT", c.LotNumber ?? "—"),
            ("MATERIAL", $"{c.MaterialCode} · {c.MaterialName}"),
            ("QUANTITY", $"{c.Quantity.ToString("#,##0.###", CultureInfo.InvariantCulture)} {c.UomCode}"),
            ("SPECIFICATION", $"version {c.SpecVersion}{(c.SpecReleasedAt is { } at ? $", released {Local(at, zone)[..10]}" : "")}"),
        };
        if (c.ProducedByJobNumber is { } job)
        {
            facts.Add(("MADE IN JOB", job));
        }

        if (c.SupplierLot is { } supplier)
        {
            facts.Add(("SUPPLIER LOT", supplier));
        }

        if (c.ExpiresAt is { } expires)
        {
            facts.Add(("EXPIRES", Local(expires, zone)[..10]));
        }

        if (c.Reference is { } reference)
        {
            facts.Add(("REFERENCE", reference));
        }

        var y = Margin + 40;
        for (var i = 0; i < facts.Count; i++)
        {
            var x = Margin + (i % 2) * 260;
            Text(g, facts[i].Item1, x, y, 250, LabelFont, MutedColor);
            Text(g, facts[i].Item2, x, y + 9, 250, BodyFont, InkColor);
            if (i % 2 == 1 || i == facts.Count - 1)
            {
                y += 28;
            }
        }

        return y + 6;
    }

    private static double Columns_(XGraphics g, double y)
    {
        foreach (var (title, x, width) in Columns)
        {
            Text(g, title, x, y, width, LabelFont, MutedColor);
        }

        g.DrawLine(new XPen(RuleColor, 0.6), Margin, y + 12, PageWidth - Margin, y + 12);
        return y + 16;
    }

    private static void Row(XGraphics g, CertificateLineDto l, double y)
    {
        var unit = l.UomCode is { } u ? $" {u}" : "";
        Text(g, $"{l.CharacteristicCode} · {l.CharacteristicName}", Columns[0].X, y + 2, Columns[0].Width, BodyFont, InkColor);
        Text(g, l.Requirement + (l.Measured ? unit : ""), Columns[1].X, y + 2, Columns[1].Width, BodyFont, InkColor);
        Text(g, Result(l, unit), Columns[2].X, y + 2, Columns[2].Width, BodyFont, InkColor);
        var (word, color) = l.Outcome switch
        {
            "pass" => ("PASS", InkColor),
            "fail" => ("FAIL", FailColor),
            "not_judged" => ("NOT JUDGED", MutedColor),
            _ => ("NO RESULT", FailColor),
        };
        Text(g, word, Columns[3].X, y + 2, Columns[3].Width, StrongFont, color);
        g.DrawLine(new XPen(RuleColor, 0.4), Margin, y + RowHeight - 3, PageWidth - Margin, y + RowHeight - 3);
    }

    // "mean 3.31 (3.28 to 3.35), 4 results" or "2 defects in 500 inspected, 1 result"; "from its job" when the lot had none of its own.
    private static string Result(CertificateLineDto l, string unit)
    {
        if (l.Results == 0)
        {
            return "—";
        }

        var count = l.Results == 1 ? "1 result" : $"{l.Results} results";
        var from = l.Source == "job" ? ", from its job" : "";
        if (!l.Measured)
        {
            return $"{l.Defects ?? 0} defects in {l.Inspected ?? 0} inspected, {count}{from}";
        }

        string N(decimal? v) => v is { } d ? decimal.Round(d, l.Decimals).ToString(CultureInfo.InvariantCulture) : "—";
        return l.Results == 1 && l.Min == l.Max
            ? $"{N(l.Mean)}{unit}{from}"
            : $"mean {N(l.Mean)}{unit} ({N(l.Min)} to {N(l.Max)}), {count}{from}";
    }

    private static void Text(XGraphics g, string value, double x, double y, double width, XFont font, XColor color) =>
        g.DrawString(value, font, new XSolidBrush(color), new XRect(x, y, width, font.Size + 6), XStringFormats.TopLeft);

    private static string Local(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
