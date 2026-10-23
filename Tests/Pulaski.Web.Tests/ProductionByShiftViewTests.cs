using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>How the production-by-shift report groups and names its shifts (D-044).</summary>
public class ProductionByShiftViewTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly TimeSpan Cdt = TimeSpan.FromHours(-5);

    private static ShiftProductionRowDto Row(string? shift, string line, string uom, decimal good, decimal scrap = 0) => new()
    {
        ProductionDate = Monday,
        ScheduleCode = shift is null ? null : "2X12",
        ShiftCode = shift,
        ShiftName = shift == "N" ? "Nights" : null,
        Crew = shift == "N" ? "BLUE" : null,
        ShiftStart = shift is null ? null : new DateTimeOffset(2026, 10, 19, 18, 0, 0, Cdt),
        ShiftEnd = shift is null ? null : new DateTimeOffset(2026, 10, 20, 6, 0, 0, Cdt),
        LineCode = line,
        UomCode = uom,
        GoodQty = good,
        ScrapQty = scrap,
    };

    [Fact]
    public void Rows_group_by_shift_with_totals_per_unit()
    {
        var groups = ProductionByShiftView.Groups([Row("N", "EXT-01", "kg", 100, 5), Row("N", "EXT-02", "kg", 50), Row("N", "PACK", "ea", 7), Row(null, "EXT-01", "kg", 3)]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Mon 19 Oct 2026 · Nights (N, 2X12) 18:00–06:00 +1 · BLUE", groups[0].Label);
        Assert.Equal([("ea", 7m, 0m), ("kg", 150m, 5m)], groups[0].Totals);
        Assert.Equal("Mon 19 Oct 2026 · outside shifts", groups[1].Label);
        Assert.Equal("1,234.5", ProductionByShiftView.Qty(1234.5m));
    }
}
