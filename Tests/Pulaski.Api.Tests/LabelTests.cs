using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Pulaski.Api.WorkOrders;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;
using ZXing;
using ZXing.Common;

namespace Pulaski.Api.Tests;

/// <summary>GS1 barcodes that scan back to what they carry, and labels rendered from templates and sent to printers (#75).</summary>
public sealed class LabelTests : IDisposable
{
    private static readonly IReadOnlyList<Gs1Element> Lot =
        [new("01", "09501101530003"), new("17", "271231"), new("3101", "000125"), new("10", "AB-12")];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulaski-labels-" + Guid.NewGuid().ToString("N"));

    public LabelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "templates", "t"));
        Directory.CreateDirectory(Path.Combine(_root, "templates", "c"));
        Directory.CreateDirectory(Path.Combine(_root, "drop"));
        File.WriteAllText(Path.Combine(_root, "templates", "t", "lot.zpl"), "^XA^FD{{Title}}^FS^FD{{Code}}^FS^XZ");
        File.WriteAllText(Path.Combine(_root, "templates", "t", "lot.gs1_128.zpl"), "^XA^BCN^FD{{Gs1Zpl128}}^FS^FD{{Gs1Hri}}^FS^XZ");
        File.WriteAllText(Path.Combine(_root, "templates", "c", "lot.csv"), "Title,Material\n{{Title}},{{MaterialName}}\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Gs1_128_and_gs1_datamatrix_scan_back_to_their_elements()
    {
        var linear = Decode(BarcodeDrawing.Gs1128Matrix(Lot), BarcodeFormat.CODE_128);
        Assert.Equal(Lot, Gs1.Parse("]C1" + linear));

        var matrix = Decode(BarcodeDrawing.Gs1DataMatrixMatrix(Lot), BarcodeFormat.DATA_MATRIX);
        Assert.Equal(Lot, Gs1.Parse("]d2" + matrix));
    }

    [Fact]
    public void Templates_fill_in_the_labels_values_escaped_for_their_language_and_pick_the_sites_format()
    {
        var templates = Templates();
        var gs1 = new LabelContent("Lot", "R^1", "PUL1L7", ["PVC"], "gs1_128", Lot) { Fields = new Dictionary<string, string?> { ["MaterialName"] = "PVC, white" } };

        var (zpl, extension) = templates.Render("t", "lot", gs1);
        Assert.Equal("zpl", extension);
        Assert.Equal("^XA^BCN^FD>;>80109501101530003172712313101000125" + "10AB-12^FS^FD(01)09501101530003(17)271231(3101)000125(10)AB-12^FS^XZ", zpl);

        // Without GS1 data the plain template is used, and a caret in a value can't start a ZPL command.
        var plain = gs1 with { Format = "pulaski", Gs1 = null };
        Assert.Equal("^XA^FDR 1^FS^FDPUL1L7^FS^XZ", templates.Render("t", "lot", plain).Text);
        Assert.Throws<Pulaski.Domain.DomainRuleException>(() => templates.Render("missing", "lot", plain));
        Assert.Throws<Pulaski.Domain.DomainRuleException>(() => templates.Render("t", "container", plain));
        Assert.Equal("Title,Material\nR^1,\"PVC, white\"\n", templates.Render("c", "lot", plain).Text);
        Assert.Equal(["c", "t"], templates.Sets());
    }

    [Fact]
    public async Task Labels_go_to_a_drop_folder_as_whole_files_or_to_a_printer_over_raw_tcp()
    {
        var printing = new LabelPrinting(Templates(), Options(), TimeProvider.System);
        var label = new LabelContent("Lot", "R-1", "PUL1L7", ["PVC"]) { Fields = new Dictionary<string, string?> { ["MaterialName"] = "PVC, white" } };

        var dropped = await printing.PrintAsync(Printer("drop_folder", templateSet: "t") with { DropFolder = "bt", Method = "drop_folder" }, "lot", [label], 2, default);
        Assert.Equal(2, dropped.Labels);
        var files = Directory.GetFiles(Path.Combine(_root, "drop"));
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.EndsWith(".zpl", f, StringComparison.Ordinal));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            return await reader.ReadToEndAsync();
        });
        await printing.PrintAsync(Printer("raw_tcp", templateSet: "t") with { Host = "127.0.0.1", Port = port }, "lot", [label], 1, default);
        Assert.Equal("^XA^FDR-1^FS^FDPUL1L7^FS^XZ", await received);

        await Assert.ThrowsAsync<Pulaski.Domain.DomainRuleException>(() =>
            printing.PrintAsync(Printer("drop_folder", templateSet: "t") with { DropFolder = "nowhere", Method = "drop_folder" }, "lot", [label], 1, default));
    }

    private LabelTemplates Templates() => new(Options(), new Environment(_root));

    private IOptions<LabelPrintingOptions> Options() => Microsoft.Extensions.Options.Options.Create(new LabelPrintingOptions
    {
        TemplateDirectory = Path.Combine(_root, "templates"),
        DropFolders = new() { ["bt"] = Path.Combine(_root, "drop") },
        ConnectTimeoutSeconds = 2,
    });

    private static LabelPrinterDto Printer(string method, string templateSet) => new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = Guid.CreateVersion7(),
        Code = "ZEB-1",
        Name = "Zebra",
        Method = method,
        TemplateSet = templateSet,
        IsActive = true,
    };

    private static string Decode(BitMatrix matrix, BarcodeFormat format)
    {
        // Scaled up with a quiet zone, as a printed label would be.
        const int Scale = 4;
        const int Quiet = 10;
        var width = (matrix.Width + 2 * Quiet) * Scale;
        var height = (Math.Max(matrix.Height, format == BarcodeFormat.CODE_128 ? 20 : matrix.Height) + 2 * Quiet) * Scale;
        var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var mx = x / Scale - Quiet;
                var my = format == BarcodeFormat.CODE_128 ? 0 : y / Scale - Quiet;
                if (mx >= 0 && mx < matrix.Width && my >= 0 && my < matrix.Height && (format != BarcodeFormat.CODE_128 || (y / Scale >= Quiet && y / Scale < height / Scale - Quiet)) && matrix[mx, my])
                {
                    pixels[y * width + x] = 0;
                }
            }
        }

        var reader = new BarcodeReaderGeneric { Options = new ZXing.Common.DecodingOptions { PossibleFormats = [format], TryHarder = true } };
        return reader.Decode(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8))?.Text
            ?? throw new InvalidOperationException("The barcode didn't decode.");
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Pulaski.Api";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }
}
