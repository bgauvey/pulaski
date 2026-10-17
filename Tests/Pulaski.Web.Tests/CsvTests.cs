using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

/// <summary>CSV for spreadsheet exports (D-030).</summary>
public class CsvTests
{
    [Fact]
    public void Fields_are_quoted_when_needed_and_numbers_and_times_are_invariant()
    {
        var csv = Csv.Write(
            ["Lot", "Note", "Quantity", "When"],
            [
                ["R-1", "Wet, torn \"bag\"", 1234.500000m, new DateTimeOffset(2026, 10, 16, 14, 5, 0, TimeSpan.Zero)],
                ["R-2", null, -3m, null],
            ]);

        Assert.Equal(
            "Lot,Note,Quantity,When\r\nR-1,\"Wet, torn \"\"bag\"\"\",1234.5,2026-10-16 14:05\r\nR-2,,-3,\r\n",
            csv);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@x", "'@x")]
    [InlineData("WO-1001", "WO-1001")]
    public void Text_that_a_spreadsheet_would_run_as_a_formula_is_kept_as_text(string value, string expected) =>
        Assert.Equal($"A\r\n{expected}\r\n", Csv.Write(["A"], [[value]]));

    [Fact]
    public void File_names_carry_what_the_site_and_the_time() =>
        Assert.Equal("stock-PLT1-20261016-1405.csv", Csv.FileName("stock", "PLT1", new DateTimeOffset(2026, 10, 16, 14, 5, 0, TimeSpan.Zero)));
}
