using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Inventory;

namespace Pulaski.Web.Tests;

/// <summary>Grouping the stock list and laying out a trace (D-027).</summary>
public class InventoryViewTests
{
    private static MaterialStockDto Stock(string code, string? classCode, string? className, int lots = 0) => new()
    {
        MaterialId = Guid.CreateVersion7(),
        Code = code,
        Name = code,
        MaterialType = "raw",
        MaterialClassCode = classCode,
        MaterialClassName = className,
        BaseUomId = Guid.CreateVersion7(),
        BaseUomCode = "kg",
        LotCount = lots,
    };

    private static LotTraceNodeDto Node(Guid id, Guid? via, int depth, string number, string material = "PVC") => new()
    {
        LotId = id,
        ViaLotId = via,
        Depth = depth,
        LotNumber = number,
        MaterialCode = material,
        MaterialName = material,
        Status = "available",
        UomCode = "kg",
    };

    [Fact]
    public void Stock_groups_by_class_name_with_no_class_last_and_codes_in_order()
    {
        var groups = InventoryView.Group([
            Stock("TIO2", "ADDITIVE", "Additives", lots: 2),
            Stock("BOX-1", null, null),
            Stock("PVC-B", "RESIN", "Resins", lots: 1),
            Stock("PVC-A", "RESIN", "Resins"),
        ]);

        Assert.Equal(["Additives", "Resins", "No class"], groups.Select(g => g.Label));
        Assert.Equal(["PVC-A", "PVC-B"], groups[1].Items.Select(i => i.Code));
        Assert.Equal((1, 1), (groups[1].InStock, groups[1].Lots));
        Assert.Equal(InventoryView.NoClass, groups[2].Key);
    }

    [Fact]
    public void A_trace_nests_each_lot_under_the_lot_it_was_reached_from()
    {
        var root = Guid.CreateVersion7();
        var blend = Guid.CreateVersion7();
        var profileB = Guid.CreateVersion7();
        var profileA = Guid.CreateVersion7();
        var pallet = Guid.CreateVersion7();

        var rows = InventoryView.Tree([
            Node(root, null, 0, "R-1"),
            Node(profileB, blend, 2, "P-2", "PRF"),
            Node(blend, root, 1, "B-1", "BLEND"),
            Node(pallet, profileA, 3, "PAL-1", "PRF"),
            Node(profileA, blend, 2, "P-1", "PRF"),
        ]);

        Assert.Equal(
            [("R-1", 0), ("B-1", 1), ("P-1", 2), ("PAL-1", 3), ("P-2", 2)],
            rows.Select(r => (r.Node.LotNumber, r.Indent)));
        Assert.Empty(InventoryView.Tree([]));
    }

    [Theory]
    [InlineData("R-24-0917", false, "RM", "R-24-0917")]
    [InlineData("@RM-STORE", true, "RM-STORE", "Balance at RM-STORE")]
    [InlineData("@RM-STORE", true, null, "Balance at RM-STORE")]
    public void A_bulk_balance_is_named_by_its_location(string number, bool bulk, string? location, string expected) =>
        Assert.Equal(expected, InventoryView.LotLabel(number, bulk, location));

    [Fact]
    public void Quantities_format_with_separators_and_signs()
    {
        Assert.Equal("1,234.5", InventoryView.Qty(1234.500000m));
        Assert.Equal("+5", InventoryView.Delta(5m));
        Assert.Equal("−12.25", InventoryView.Delta(-12.25m));
        Assert.Equal(("Written up", "Written off"), (InventoryView.EventLabel("adjusted"), InventoryView.EventLabel("scrapped")));
    }

    [Fact]
    public void Expiry_is_soon_within_the_window_or_past()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.True(InventoryView.ExpiresSoon(now.AddDays(-1), now));
        Assert.True(InventoryView.ExpiresSoon(now.AddDays(13), now));
        Assert.False(InventoryView.ExpiresSoon(now.AddDays(30), now));
        Assert.False(InventoryView.ExpiresSoon(null, now));
    }
}
