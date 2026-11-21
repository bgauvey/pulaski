using System.Xml.Linq;
using Pulaski.Application.Inventory;

namespace Pulaski.Application.Tests;

/// <summary>BarTender's BTXML scripts (#168).</summary>
public class BtxmlTests
{
    [Fact]
    public void The_standard_template_fills_in_a_print_script_with_every_value_escaped()
    {
        var values = new Dictionary<string, string?>
        {
            ["Title"] = "R-1 <A&B> \"x\"",
            ["Code"] = "PUL1L7",
            ["Gs1Hri"] = "(10)AB",
            ["Gs1Data"] = "10AB\u001d17",
            ["Gs1Zpl128"] = ">;>8",
        };
        var script = XDocument.Parse(Btxml.Render(Btxml.StandardTemplate, values, @"C:\Labels\Lot.btw", "Zebra & Co", 2, "Pulaski-lot-1"));

        Assert.Equal("XMLScript", script.Root!.Name.LocalName);
        Assert.Equal(@"C:\Labels\Lot.btw", script.Descendants("Format").Single().Value);
        Assert.Equal(("Zebra & Co", "2"), (script.Descendants("Printer").Single().Value, script.Descendants("IdenticalCopiesOfLabel").Single().Value));
        var named = script.Descendants("NamedSubString").ToDictionary(e => (string)e.Attribute("Name")!, e => e.Element("Value")!.Value);
        Assert.Equal("R-1 <A&B> \"x\"", named["Title"]);
        Assert.Equal("(10)AB", named["Gs1Hri"]);
        Assert.False(named.ContainsKey("Gs1Data") || named.ContainsKey("Gs1Zpl128"));

        // Without a printer the document's own prints; a value can be placed by name too.
        var plain = XDocument.Parse(Btxml.Render("<XMLScript><Command><Print><Format>{{BtwPath}}</Format><X>{{Code}}{{Missing}}</X>{{PrintSetup}}</Print></Command></XMLScript>",
            values, "a.btw", null, 1, "j"));
        Assert.Empty(plain.Descendants("Printer"));
        Assert.Equal("PUL1L7", plain.Descendants("X").Single().Value);
    }

    [Fact]
    public void A_document_is_the_operations_own_else_its_kinds_else_the_default()
    {
        var files = new Dictionary<string, string> { ["default"] = "any.btw", ["lot"] = "lot.btw", ["lot.produced"] = "made.btw" };
        Assert.Equal("made.btw", Btxml.BtwFor(files, "lot.produced"));
        Assert.Equal("lot.btw", Btxml.BtwFor(files, "lot.received"));
        Assert.Equal("any.btw", Btxml.BtwFor(files, "container"));
        Assert.Null(Btxml.BtwFor(new Dictionary<string, string> { ["lot"] = "lot.btw" }, "location"));

        Assert.True(Btxml.IsOperation("lot.produced") && Btxml.IsOperation("default"));
        Assert.False(Btxml.IsOperation("Lot") || Btxml.IsOperation("lot.") || Btxml.IsOperation("a.b.c"));
        Assert.Null(Btxml.PathProblem(@"\\server\labels\Lot.BTW"));
        Assert.NotNull(Btxml.PathProblem("lot.txt"));
        Assert.NotNull(Btxml.PathProblem(" "));
    }

    [Fact]
    public void A_template_must_be_a_btxml_print_script_and_bartenders_errors_are_read_from_its_answer()
    {
        Assert.Empty(Btxml.Problems(Btxml.StandardTemplate));
        Assert.NotEmpty(Btxml.Problems(""));
        Assert.NotEmpty(Btxml.Problems("<XMLScript><Command>"));
        Assert.NotEmpty(Btxml.Problems("<Other><Print><Format>{{BtwPath}}</Format></Print></Other>"));
        Assert.NotEmpty(Btxml.Problems("<XMLScript><Command><Print/></Command></XMLScript>"));

        Assert.Equal("Could not open the format.", Btxml.ErrorIn(
            "<XMLScript><Command><Print><Message ID=\"1\" Severity=\"Error\"><Text>Could not open the format.</Text></Message></Print></Command></XMLScript>"));
        Assert.Null(Btxml.ErrorIn("<XMLScript><Command><Print><Message Severity=\"Information\"><Text>OK</Text></Message></Print></Command></XMLScript>"));
        Assert.Null(Btxml.ErrorIn("OK"));
        Assert.Null(Btxml.ErrorIn("<broken"));
    }
}
