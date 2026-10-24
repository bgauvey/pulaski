using PdfSharp.Drawing;
using ZXing;
using ZXing.OneD;

namespace Pulaski.Api.WorkOrders;

/// <summary>Code 128 barcodes drawn as PDF rectangles, for shop orders and lot and container labels (D-059).</summary>
internal static class BarcodeDrawing
{
    /// <summary>Draws <paramref name="value"/> to fill the box, one rectangle per bar, so it stays sharp at any print size.</summary>
    public static void Code128(XGraphics graphics, string value, double x, double y, double width, double height)
    {
        var matrix = new Code128Writer().encode(value, BarcodeFormat.CODE_128, 1, 1);
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
