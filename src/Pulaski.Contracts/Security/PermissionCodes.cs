namespace Pulaski.Contracts.Security;

/// <summary>
/// The permission codes the API checks (D-037), for clients that show or hide actions. They mirror
/// <c>Pulaski.Domain.Security.Permissions</c>, which a test keeps in step.
/// </summary>
public static class PermissionCodes
{
    public const string SchedulePlan = "schedule.plan";
    public const string WorkOrderManage = "work_order.manage";
    public const string WorkOrderClose = "work_order.close";
    public const string RateModify = "rate.modify";
    public const string JobExecute = "job.execute";
    public const string JobReopen = "job.reopen";
    public const string ProductionReport = "production.report";
    public const string LotReceive = "lot.receive";
    public const string LotMove = "lot.move";
    public const string LotShip = "lot.ship";
    public const string LotHold = "lot.hold";
    public const string InventoryCorrect = "inventory.correct";
    public const string StockCount = "stock_count.manage";
    public const string PartialBatchDecide = "partial_batch.decide";
    public const string Materials = "materials.manage";
    public const string Processes = "processes.manage";
    public const string Units = "units.manage";
    public const string Reasons = "reasons.manage";
    public const string Equipment = "equipment.manage";
    public const string SiteSettings = "site_settings.manage";
    public const string Security = "security.manage";
}
