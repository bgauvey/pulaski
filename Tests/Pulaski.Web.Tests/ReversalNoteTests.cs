using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Inventory;

namespace Pulaski.Web.Tests;

/// <summary>What the reverse drawer says a reversal will do (D-063).</summary>
public class ReversalNoteTests
{
    private static readonly LotSummaryDto Lot = new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = Guid.CreateVersion7(),
        MaterialId = Guid.CreateVersion7(),
        UomId = Guid.CreateVersion7(),
        Origin = "received",
        LotNumber = "R-1",
        MaterialCode = "PVC",
        MaterialName = "PVC",
        Status = "available",
        UomCode = "kg",
    };

    private static LotEventDto E(string kind, decimal? delta = null, Guid? related = null) =>
        new() { Id = Guid.CreateVersion7(), Kind = kind, QtyDelta = delta, UomCode = "kg", JobNumber = "WO-1-10", RelatedLotId = related };

    [Fact]
    public void Each_kind_of_reversal_says_what_it_puts_back()
    {
        Assert.StartsWith("The 30 kg goes back to the lot", InventoryView.ReversalNote(E("consumed", -30m), Lot), StringComparison.Ordinal);
        Assert.Contains("new lot is cancelled", InventoryView.ReversalNote(E("split", 5m), Lot), StringComparison.Ordinal);
        Assert.Contains("merge comes apart", InventoryView.ReversalNote(E("merged", 5m), Lot), StringComparison.Ordinal);
        Assert.Contains("balance it came from", InventoryView.ReversalNote(E("moved", -5m, Guid.CreateVersion7()), Lot), StringComparison.Ordinal);
        Assert.Contains("reported output stays", InventoryView.ReversalNote(E("produced", 5m), Lot), StringComparison.Ordinal);
        Assert.Contains("as if it never happened", InventoryView.ReversalNote(E("adjusted", 5m), Lot), StringComparison.Ordinal);
    }
}
