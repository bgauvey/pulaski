using PdfSharp.Drawing;
using Pulaski.Domain.Inventory;
using ZXing;
using ZXing.Common;
using ZXing.Datamatrix;
using ZXing.OneD;

namespace Pulaski.Api.WorkOrders;

/// <summary>Barcodes drawn as PDF rectangles, for shop orders and labels: Code 128 (D-059), and GS1-128 and GS1 DataMatrix (#75).</summary>
internal static class BarcodeDrawing
{
    /// <summary>The character ZXing's Code 128 writer encodes as FNC1.</summary>
    private const char Fnc1 = 'ñ';

    /// <summary>Draws <paramref name="value"/> to fill the box, one rectangle per bar, so it stays sharp at any print size.</summary>
    public static void Code128(XGraphics graphics, string value, double x, double y, double width, double height) =>
        Draw(graphics, Code128Matrix(value), x, y, width, height);

    /// <summary>A GS1-128 barcode: FNC1 first, and after each variable-length field but the last.</summary>
    public static void Gs1128(XGraphics graphics, IReadOnlyList<Gs1Element> elements, double x, double y, double width, double height) =>
        Draw(graphics, Gs1128Matrix(elements), x, y, width, height);

    /// <summary>A GS1 DataMatrix, square, <paramref name="size"/> points a side.</summary>
    public static void Gs1DataMatrix(XGraphics graphics, IReadOnlyList<Gs1Element> elements, double x, double y, double size) =>
        Draw(graphics, Gs1DataMatrixMatrix(elements), x, y, size, size);

    public static BitMatrix Code128Matrix(string value) => new Code128Writer().encode(value, BarcodeFormat.CODE_128, 1, 1);

    public static BitMatrix Gs1128Matrix(IReadOnlyList<Gs1Element> elements) =>
        new Code128Writer().encode(Fnc1 + Gs1.Data(elements, Fnc1.ToString()), BarcodeFormat.CODE_128, 1, 1);

    public static BitMatrix Gs1DataMatrixMatrix(IReadOnlyList<Gs1Element> elements) =>
        new DataMatrixWriter().encode(
            Gs1.Data(elements, Gs1.GroupSeparator.ToString()), BarcodeFormat.DATA_MATRIX, 0, 0,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.GS1_FORMAT] = true, [EncodeHintType.MARGIN] = 0 });

    private static void Draw(XGraphics graphics, BitMatrix matrix, double x, double y, double width, double height)
    {
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
}
