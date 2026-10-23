using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Pulaski.Contracts.WorkOrders;
using ZXing;
using ZXing.OneD;

namespace Pulaski.Api.WorkOrders;

public sealed class ShopOrderPdfRenderer
{
    private const double PageWidth = 612;
    private const double PageHeight = 792;
    private const double Margin = 36;
    private static readonly XColor InkColor = XColor.FromArgb(35, 52, 69);
    private static readonly XColor MutedColor = XColor.FromArgb(93, 106, 119);
    private static readonly XColor RuleColor = XColor.FromArgb(202, 210, 216);
    private static readonly XColor AccentColor = XColor.FromArgb(20, 83, 105);
    private static readonly XFont TitleFont;
    private static readonly XFont HeaderFont;
    private static readonly XFont LabelFont;
    private static readonly XFont BodyFont;
    private static readonly XFont SmallFont;

    static ShopOrderPdfRenderer()
    {
        GlobalFontSettings.FontResolver ??= new RobotoFontResolver();
        TitleFont = new XFont("Roboto", 18, XFontStyleEx.Bold);
        HeaderFont = new XFont("Roboto", 11, XFontStyleEx.Bold);
        LabelFont = new XFont("Roboto", 8, XFontStyleEx.Bold);
        BodyFont = new XFont("Roboto", 10, XFontStyleEx.Regular);
        SmallFont = new XFont("Roboto", 8, XFontStyleEx.Regular);
    }

    public byte[] Render(ShopOrderDto shopOrder)
    {
        using var document = new PdfDocument();
        document.Info.Title = $"Shop order {shopOrder.Order.OrderNumber}";
        document.Info.Subject = "Work order job traveler";
        document.Info.Creator = "Pulaski";

        if (shopOrder.Jobs.Count == 0)
        {
            var page = StartPage(document);
            DrawOrderHeader(page.Graphics, shopOrder, null, false);
            DrawSectionTitle(page.Graphics, "Jobs", Margin, 250, PageWidth - 2 * Margin);
            DrawText(page.Graphics, "No jobs are currently planned for this work order.", Margin, 268, PageWidth - 2 * Margin, BodyFont, InkColor);
            DrawSectionTitle(page.Graphics, "Specifications", Margin, 306, PageWidth - 2 * Margin);
            DrawText(page.Graphics, "No released product specifications are configured in Pulaski. Verify the approved specification before production.",
                Margin, 324, PageWidth - 2 * Margin, SmallFont, MutedColor);
            page.Graphics.Dispose();
        }
        else
        {
            foreach (var job in shopOrder.Jobs)
            {
                var writer = new JobPageWriter(document, shopOrder, job);
                writer.Render();
            }
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static Page StartPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.Letter;
        return new Page(page, XGraphics.FromPdfPage(page));
    }

    private static void DrawOrderHeader(XGraphics graphics, ShopOrderDto shopOrder, ShopOrderJobDto? job, bool continued)
    {
        var order = shopOrder.Order;
        DrawText(graphics, continued ? "SHOP ORDER / JOB TRAVELER — CONTINUED" : "SHOP ORDER / JOB TRAVELER",
            Margin, 25, PageWidth - 2 * Margin, TitleFont, AccentColor);
        DrawText(graphics, $"{order.SiteCode} · Site time: {shopOrder.SiteTimeZone}",
            Margin, 51, PageWidth - 2 * Margin, SmallFont, MutedColor);
        DrawLine(graphics, Margin, 68, PageWidth - Margin, 68, RuleColor);
        DrawField(graphics, "WORK ORDER", order.OrderNumber, Margin, 76, 250);
        DrawField(graphics, "JOB", job?.JobNumber ?? "Not assigned", 306, 76, 270);
        DrawField(graphics, "MATERIAL", $"{order.MaterialCode} · {order.MaterialName}", Margin, 113, 360);
        DrawField(graphics, "ORDER QUANTITY", $"{Format(order.PlannedQty)} {order.UomCode}", 406, 113, 170);

        if (job is null)
        {
            return;
        }

        DrawBarcode(graphics, job.BarcodeValue, Margin, 151, PageWidth - 2 * Margin, 40);
        DrawText(graphics, job.BarcodeValue, Margin, 194, PageWidth - 2 * Margin, SmallFont, MutedColor);
        var operation = job.OperationNumber is { } number
            ? $"OP-{number.ToString(CultureInfo.InvariantCulture)} · {job.OperationName}"
            : "Operation not assigned";
        DrawFittedText(graphics, operation, Margin, 212, PageWidth - 2 * Margin, HeaderFont, InkColor);
        DrawLine(graphics, Margin, 235, PageWidth - Margin, 235, RuleColor);
    }

    private static void DrawBarcode(XGraphics graphics, string value, double x, double y, double width, double height)
    {
        var matrix = new Code128Writer().encode(value, BarcodeFormat.CODE_128, 640, 80);
        var moduleWidth = width / matrix.Width;
        var moduleHeight = height / matrix.Height;
        for (var row = 0; row < matrix.Height; row++)
        {
            var runStart = -1;
            for (var column = 0; column <= matrix.Width; column++)
            {
                var black = column < matrix.Width && matrix[column, row];
                if (black && runStart < 0)
                {
                    runStart = column;
                }
                else if (!black && runStart >= 0)
                {
                    graphics.DrawRectangle(XBrushes.Black, x + runStart * moduleWidth, y + row * moduleHeight,
                        (column - runStart) * moduleWidth, moduleHeight);
                    runStart = -1;
                }
            }
        }
    }

    private static void DrawSectionTitle(XGraphics graphics, string title, double x, double y, double width)
    {
        DrawText(graphics, title.ToUpperInvariant(), x, y, width, LabelFont, AccentColor);
        DrawLine(graphics, x, y + 14, x + width, y + 14, RuleColor);
    }

    private static void DrawField(XGraphics graphics, string label, string value, double x, double y, double width)
    {
        DrawText(graphics, label, x, y, width, LabelFont, MutedColor);
        DrawFittedText(graphics, value, x, y + 13, width, BodyFont, InkColor);
    }

    private static void DrawFittedText(XGraphics graphics, string value, double x, double y, double width, XFont font, XColor color)
    {
        var size = font.Size;
        while (size > 7 && graphics.MeasureString(value, font).Width > width)
        {
            size -= 0.5;
            font = new XFont("Roboto", size, font.Style);
        }

        DrawText(graphics, value, x, y, width, font, color);
    }

    private static void DrawText(XGraphics graphics, string value, double x, double y, double width, XFont font, XColor color) =>
        graphics.DrawString(value, font, new XSolidBrush(color), new XRect(x, y, width, font.Size + 6), XStringFormats.TopLeft);

    private static void DrawLine(XGraphics graphics, double x1, double y1, double x2, double y2, XColor color) =>
        graphics.DrawLine(new XPen(color, 0.7), x1, y1, x2, y2);

    private static string Format(decimal value) => value.ToString("#,##0.###", CultureInfo.InvariantCulture);

    private static string FormatTime(DateTimeOffset? value, string timeZone)
    {
        if (value is not { } time)
        {
            return "Not scheduled";
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var siteTime = TimeZoneInfo.ConvertTime(time, zone);
        return $"{siteTime:yyyy-MM-dd HH:mm zzz} · {timeZone}";
    }

    private sealed class JobPageWriter(PdfDocument document, ShopOrderDto shopOrder, ShopOrderJobDto job)
    {
        private Page _page = null!;
        private double _y;

        public void Render()
        {
            NewPage(false);
            DrawFields();
            DrawOrderNote();
            DrawOperationDescription();
            DrawComponents();
            DrawSpecificationNotice();
            DrawFooter();
            _page.Graphics.Dispose();
        }

        private void NewPage(bool continued)
        {
            if (_page is not null)
            {
                _page.Graphics.Dispose();
            }

            _page = StartPage(document);
            DrawOrderHeader(_page.Graphics, shopOrder, job, continued);
            if (continued)
            {
                DrawText(_page.Graphics, "CONTINUED", Margin, 243, PageWidth - 2 * Margin, LabelFont, MutedColor);
                _y = 265;
            }
            else
            {
                _y = 250;
            }
        }

        private void DrawFields()
        {
            var order = shopOrder.Order;
            var line = job.WaitingForLineAssignment
                ? $"Waiting for line assignment · {job.EquipmentCode}"
                : job.EquipmentCode;
            if (job.CandidateLines.Count > 0)
            {
                line += $" (possible: {string.Join(", ", job.CandidateLines)})";
            }

            var scheduleLabel = job.UsesProjectedSchedule ? "PROJECTED SCHEDULE" : "PUBLISHED BASELINE";
            DrawField(_page.Graphics, "SCHEDULED LINE / DEPARTMENT", line, Margin, _y, 300);
            DrawField(_page.Graphics, scheduleLabel, FormatTime(job.ScheduledStart, shopOrder.SiteTimeZone), 354, _y, 222);
            _y += 45;
            DrawField(_page.Graphics, "SCHEDULED FINISH", FormatTime(job.ScheduledEnd, shopOrder.SiteTimeZone), Margin, _y, 300);
            DrawField(_page.Graphics, "PLANNED JOB QUANTITY", job.PlannedQty is { } qty ? $"{Format(qty)} {job.UomCode}" : "Not specified",
                354, _y, 222);
            _y += 45;
            var rate = job.RatePerHour is { } rateValue
                ? $"{Format(rateValue)} {job.RateUomCode ?? job.UomCode}/h · {job.RateSource ?? "run standard"}"
                : job.WaitingForLineAssignment ? "Set after line assignment" : "Not available";
            DrawField(_page.Graphics, "RUN RATE", rate, Margin, _y, 300);
            DrawField(_page.Graphics, "JOB STATUS / TYPE", $"{job.Status} · {job.JobType}", 354, _y, 222);
            DrawLine(_page.Graphics, Margin, _y + 38, PageWidth - Margin, _y + 38, RuleColor);
            _y += 52;
            DrawField(_page.Graphics, "WORK ORDER DUE", FormatTime(order.DueAt, shopOrder.SiteTimeZone), Margin, _y, 300);
            DrawField(_page.Graphics, "WORK ORDER STATUS / PRIORITY", $"{order.Status} · {order.Priority}", 354, _y, 222);
            DrawLine(_page.Graphics, Margin, _y + 38, PageWidth - Margin, _y + 38, RuleColor);
            _y += 52;
        }

        private void DrawOrderNote()
        {
            DrawParagraphSection("SPECIAL INSTRUCTIONS", shopOrder.Order.Notes ?? "No special instructions.");
        }

        private void DrawOperationDescription()
        {
            DrawParagraphSection("OPERATION INSTRUCTIONS", job.OperationDescription ?? "No operation description.");
        }

        private void DrawParagraphSection(string heading, string text)
        {
            EnsureRoom(42);
            DrawSectionTitle(_page.Graphics, heading, Margin, _y, PageWidth - 2 * Margin);
            _y += 22;
            foreach (var line in Wrap(text, BodyFont, PageWidth - 2 * Margin))
            {
                EnsureRoom(17);
                DrawText(_page.Graphics, line, Margin, _y, PageWidth - 2 * Margin, BodyFont, InkColor);
                _y += 14;
            }

            _y += 12;
        }

        private void DrawComponents()
        {
            EnsureRoom(40);
            DrawSectionTitle(_page.Graphics, $"BILL OF MATERIALS · VERSION {shopOrder.Order.BomVersionNumber?.ToString(CultureInfo.InvariantCulture) ?? "NOT SET"}",
                Margin, _y, PageWidth - 2 * Margin);
            _y += 22;
            if (job.Components.Count == 0)
            {
                EnsureRoom(18);
                DrawText(_page.Graphics, "No planned BOM components for this job.", Margin, _y, PageWidth - 2 * Margin, BodyFont, InkColor);
                _y += 22;
                return;
            }

            DrawComponentHeader();
            foreach (var component in job.Components)
            {
                var name = $"{component.MaterialCode} · {component.MaterialName}";
                var lines = Wrap(name, SmallFont, 310);
                var rowHeight = Math.Max(25, 12 * lines.Count + 7);
                EnsureRoom(rowHeight + 2);
                if (_y == 265)
                {
                    DrawComponentHeader();
                }

                foreach (var line in lines)
                {
                    DrawText(_page.Graphics, line, Margin, _y, 310, SmallFont, InkColor);
                    _y += 12;
                }

                DrawText(_page.Graphics, $"{Format(component.PlannedQty)} {component.UomCode}",
                    352, _y - 12 * lines.Count, 105, SmallFont, InkColor);
                DrawFittedText(_page.Graphics, component.ConsumptionMethod ?? "—",
                    462, _y - 12 * lines.Count, 114, SmallFont, MutedColor);
                _y = Math.Max(_y, _y - 12 * lines.Count + rowHeight);
                DrawLine(_page.Graphics, Margin, _y, PageWidth - Margin, _y, RuleColor);
                _y += 4;
            }
        }

        private void DrawComponentHeader()
        {
            DrawText(_page.Graphics, "COMPONENT", Margin, _y, 310, LabelFont, MutedColor);
            DrawText(_page.Graphics, "PLANNED", 352, _y, 105, LabelFont, MutedColor);
            DrawText(_page.Graphics, "CONSUMED BY", 462, _y, 114, LabelFont, MutedColor);
            _y += 17;
        }

        private void DrawSpecificationNotice()
        {
            EnsureRoom(62);
            DrawSectionTitle(_page.Graphics, "SPECIFICATIONS", Margin, _y, PageWidth - 2 * Margin);
            _y += 22;
            foreach (var line in Wrap(
                         "No released product specifications are configured in Pulaski. Verify the current approved specification before production.",
                         SmallFont, PageWidth - 2 * Margin))
            {
                EnsureRoom(15);
                DrawText(_page.Graphics, line, Margin, _y, PageWidth - 2 * Margin, SmallFont, MutedColor);
                _y += 12;
            }
        }

        private void DrawFooter()
        {
            DrawLine(_page.Graphics, Margin, PageHeight - 36, PageWidth - Margin, PageHeight - 36, RuleColor);
            DrawText(_page.Graphics, "Printed copy · Verify the current schedule and approved specifications before running.",
                Margin, PageHeight - 30, PageWidth - 2 * Margin, SmallFont, MutedColor);
        }

        private void EnsureRoom(double height)
        {
            if (_y + height > PageHeight - 52)
            {
                NewPage(true);
            }
        }

        private List<string> Wrap(string text, XFont font, double width)
        {
            var result = new List<string>();
            foreach (var paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
            {
                if (paragraph.Length == 0)
                {
                    result.Add("");
                    continue;
                }

                var line = "";
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Length == 0 ? word : $"{line} {word}";
                    if (_page.Graphics.MeasureString(candidate, font).Width <= width)
                    {
                        line = candidate;
                        continue;
                    }

                    if (line.Length > 0)
                    {
                        result.Add(line);
                        line = "";
                    }

                    var pieces = BreakLongWord(word, font, width).ToArray();
                    foreach (var piece in pieces.Take(pieces.Length - 1))
                    {
                        result.Add(piece);
                    }

                    line = pieces.LastOrDefault() ?? "";
                }

                if (line.Length > 0)
                {
                    result.Add(line);
                }
            }

            return result;
        }

        private IEnumerable<string> BreakLongWord(string word, XFont font, double width)
        {
            if (_page.Graphics.MeasureString(word, font).Width <= width)
            {
                yield return word;
                yield break;
            }

            var piece = "";
            foreach (var character in word)
            {
                if (_page.Graphics.MeasureString(piece + character, font).Width > width && piece.Length > 0)
                {
                    yield return piece;
                    piece = "";
                }

                piece += character;
            }

            if (piece.Length > 0)
            {
                yield return piece;
            }
        }
    }

    private sealed record Page(PdfPage PdfPage, XGraphics Graphics);

    private sealed class RobotoFontResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = ReadFont();

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            familyName.Equals("Roboto", StringComparison.OrdinalIgnoreCase)
                ? new FontResolverInfo("Roboto", mustSimulateBold: bold, mustSimulateItalic: false)
                : null;

        public byte[]? GetFont(string faceName) => faceName == "Roboto" ? FontBytes : null;

        private static byte[] ReadFont()
        {
            using var stream = typeof(ShopOrderPdfRenderer).Assembly.GetManifestResourceStream(
                "Pulaski.Api.Assets.Roboto.Roboto-Variable.ttf")
                ?? throw new InvalidOperationException("The embedded Roboto PDF font is missing.");
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }
    }
}
