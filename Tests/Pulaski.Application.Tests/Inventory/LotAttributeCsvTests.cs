using Pulaski.Application.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Lot attribute values as CSV (#78): the columns a file may have, and what a cell means.</summary>
public class LotAttributeCsvTests
{
    private static readonly string[] Codes = ["MFI", "GRADE", "MOIST-PCT"];

    [Fact]
    public void Columns_name_the_lot_and_the_sites_attributes_in_any_order_and_case()
    {
        var source = LotAttributeCsv.Parse("Lot Number;Material;moist-pct;mfi\r\nR-1;PVC;0.2;\r\nR-2;;;11.5\r\n;;;\r\n", Codes);

        Assert.Empty(source.Problems);
        Assert.Equal(2, source.Rows.Count);
        Assert.Equal(("R-1", "PVC", 2), (source.Rows[0].LotNumber, source.Rows[0].MaterialCode, source.Rows[0].Line));
        Assert.Equal(new Dictionary<string, string> { ["MOIST-PCT"] = "0.2" }, source.Rows[0].Cells);
        Assert.Equal(new Dictionary<string, string> { ["MFI"] = "11.5" }, source.Rows[1].Cells);
    }

    [Fact]
    public void A_file_without_a_lot_column_an_attribute_or_with_unknown_columns_is_refused_whole()
    {
        Assert.Contains("The required column 'lot_number' is missing.", LotAttributeCsv.Parse("mfi\r\n1\r\n", Codes).Problems);
        Assert.Contains("Name at least one lot attribute in the header.", LotAttributeCsv.Parse("lot_number\r\nR-1\r\n", Codes).Problems);
        Assert.Contains(LotAttributeCsv.Parse("lot_number,colour\r\nR-1,red\r\n", Codes).Problems, p => p.StartsWith("'colour' isn't", StringComparison.Ordinal));
        Assert.Contains("Column 'MFI' appears twice.", LotAttributeCsv.Parse("lot_number,mfi,MFI\r\nR-1,1,2\r\n", Codes).Problems);
        Assert.Contains("The file has a header but no rows.", LotAttributeCsv.Parse("lot_number,mfi\r\n", Codes).Problems);
        Assert.Equal("lot_number,material_code,MFI,GRADE\r\n", LotAttributeCsv.Template(["MFI", "GRADE"]));
    }
}
