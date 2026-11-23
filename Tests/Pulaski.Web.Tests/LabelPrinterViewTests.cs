using Pulaski.Web.Services.Localization;
using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Inventory;

namespace Pulaski.Web.Tests;

/// <summary>How label printers read on Site settings (#75, #168).</summary>
public class LabelPrinterViewTests
{
    private static LabelPrinterDto Printer(string method) => new() { Id = Guid.NewGuid(), SiteId = Guid.NewGuid(), Code = "P1", Name = "P", Method = method };

    [Fact]
    public void Printers_say_where_they_send_and_bartender_operations_read_as_label_kinds()
    {
        Assert.Equal("Raw TCP to 10.0.0.5:9100", LabelPrinterView.Sends(Text.English, Printer("raw_tcp") with { Host = "10.0.0.5", Port = 9100 }));
        Assert.Equal("Files to the 'bt' drop folder", LabelPrinterView.Sends(Text.English, Printer("drop_folder") with { DropFolder = "bt" }));
        var bartender = Printer("bartender") with
        {
            EndpointUrl = "https://bt.example.com:8080/Integration/Print/Execute",
            BartenderPrinter = "Zebra ZT411",
            BtwFiles = new Dictionary<string, string> { ["default"] = "a.btw", ["lot.produced"] = "b.btw" },
        };
        Assert.Equal("BarTender at bt.example.com:8080 to Zebra ZT411", LabelPrinterView.Sends(Text.English, bartender));
        Assert.Equal("BarTender documents: 2", LabelPrinterView.Templates(Text.English, bartender));
        Assert.Equal("zpl", LabelPrinterView.Templates(Text.English, Printer("raw_tcp") with { TemplateSet = "zpl" }));

        Assert.Equal(["default", "lot", "lot.produced"], LabelPrinterView.Operations(["default", "lot"], bartender.BtwFiles));
        Assert.Equal(("Default (any label)", "Lot labels", "Lot labels: produced"),
            (LabelPrinterView.OperationName(Text.English, "default"), LabelPrinterView.OperationName(Text.English, "lot"), LabelPrinterView.OperationName(Text.English, "lot.produced")));
    }
}
