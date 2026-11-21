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
        var printing = new LabelPrinting(Templates(), Options(), new BarTenderService(), TimeProvider.System);
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

    [Fact]
    public async Task BarTender_gets_one_btxml_script_per_label_with_the_operations_document_and_its_errors_come_back()
    {
        var bartender = new BarTenderService();
        var options = Options();
        options.Value.BarTender["BT-1"] = new BarTenderCredentials { Username = "labels", Password = "secret", Headers = { ["X-Api-Key"] = "k1" } };
        var printing = new LabelPrinting(Templates(), options, bartender, TimeProvider.System);
        var printer = Printer("bartender", templateSet: "t") with
        {
            Code = "BT-1",
            TemplateSet = null,
            EndpointUrl = "https://bartender.test/Integration/Print/Execute",
            BtxmlTemplate = Pulaski.Application.Inventory.Btxml.StandardTemplate,
            BartenderPrinter = "Zebra ZT411",
            BtwFiles = new Dictionary<string, string> { ["default"] = @"C:\Labels\Any.btw", ["lot"] = @"C:\Labels\Lot.btw" },
        };
        var a = new LabelContent("Lot", "R-1 <A&B>", "PUL1L7", ["PVC"]);
        var b = a with { Title = "R-2", Barcode = "PUL1L8" };

        var printed = await printing.PrintAsync(printer, "lot", [a, b], 3, default);
        Assert.Equal((6, "bartender"), (printed.Labels, printed.Method));
        Assert.Equal(2, bartender.Requests.Count);
        var (first, request) = bartender.Requests[0];
        var script = System.Xml.Linq.XDocument.Parse(first);
        string Of(string name) => script.Descendants(name).Single().Value;
        Assert.Equal((@"C:\Labels\Lot.btw", "Zebra ZT411", "3"), (Of("Format"), Of("Printer"), Of("IdenticalCopiesOfLabel")));
        Assert.Equal("R-1 <A&B>", script.Descendants("NamedSubString").Single(e => (string?)e.Attribute("Name") == "Title").Element("Value")!.Value);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("labels:secret")), request.Authorization);
        Assert.Equal("k1", request.ApiKey);

        // Another operation without its own document uses the default; BarTender's error message, or a refusal, comes back.
        await printing.PrintAsync(printer, "container", [a], 1, default);
        Assert.Contains(@"C:\Labels\Any.btw", bartender.Requests[^1].Body, StringComparison.Ordinal);
        bartender.Answer = (HttpStatusCode.OK, """<XMLScript Version="2.0"><Command><Print><Message ID="3201" Severity="Error"><Text>The format could not be opened.</Text></Message></Print></Command></XMLScript>""");
        var error = await Assert.ThrowsAsync<Pulaski.Domain.DomainRuleException>(() => printing.PrintAsync(printer, "lot", [a], 1, default));
        Assert.Contains("The format could not be opened.", error.Message, StringComparison.Ordinal);
        bartender.Answer = (HttpStatusCode.InternalServerError, "");
        await Assert.ThrowsAsync<Pulaski.Domain.DomainRuleException>(() => printing.PrintAsync(printer, "lot", [a], 1, default));
        await Assert.ThrowsAsync<Pulaski.Domain.DomainRuleException>(() =>
            printing.PrintAsync(printer with { BtwFiles = new Dictionary<string, string> { ["lot"] = @"C:\Labels\Lot.btw" } }, "location", [a], 1, default));
    }

    /// <summary>A BarTender web service that records what it was sent and answers as told.</summary>
    private sealed class BarTenderService : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Body, (string? Authorization, string? ApiKey) Headers)> Requests { get; } = [];

        public (HttpStatusCode Status, string Body) Answer { get; set; } = (HttpStatusCode.OK, """<XMLScript Version="2.0"><Command><Print><Message ID="0" Severity="Information"><Text>Done.</Text></Message></Print></Command></XMLScript>""");

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((await request.Content!.ReadAsStringAsync(cancellationToken),
                (request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("X-Api-Key", out var key) ? key.Single() : null)));
            return new HttpResponseMessage(Answer.Status) { Content = new StringContent(Answer.Body) };
        }
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
