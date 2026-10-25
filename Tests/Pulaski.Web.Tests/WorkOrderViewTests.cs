using Pulaski.Contracts.WorkOrders;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

/// <summary>How the work order screen words an ERP order (D-066).</summary>
public class WorkOrderViewTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private static readonly WorkOrderDto Order = new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = Guid.CreateVersion7(),
        SiteCode = "PLT1",
        OrderNumber = "WO-1",
        MaterialId = Guid.CreateVersion7(),
        MaterialCode = "M",
        MaterialName = "M",
        PlannedQty = 1100m,
        UomId = Guid.CreateVersion7(),
        UomCode = "m",
        PlannedStart = Start,
        Status = "dispatched",
        Origin = "erp",
    };

    [Fact]
    public void A_pending_change_lists_what_it_changes_and_a_cancellation_says_so()
    {
        var change = new ErpPendingChangeDto { Quantity = 1200m, UomCode = "m", PlannedStart = Start, Priority = 2 };
        var summary = WorkOrderView.PendingSummary(Order, change, null);
        Assert.StartsWith("Quantity 1,100 → 1,200 m; priority 0 → 2.", summary, StringComparison.Ordinal);
        Assert.StartsWith("Cancelled in the ERP", WorkOrderView.PendingSummary(Order, change with { Cancelled = true }, null), StringComparison.Ordinal);
        Assert.Equal("rounded up to whole batches.", WorkOrderView.Adjustment("rounded_up"));
        Assert.Equal("planned as sent.", WorkOrderView.Adjustment(null));
    }
}
