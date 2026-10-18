namespace Pulaski.Contracts.Security;

/// <summary>
/// The permission codes the API checks (D-037), for clients that show or hide actions. They mirror
/// <c>Pulaski.Domain.Security.Permissions</c>, which a test keeps in step.
/// </summary>
public static class PermissionCodes
{
    public const string OverviewView = "overview.view";
    public const string WorkOrdersView = "work_orders.view";
    public const string ScheduleView = "schedule.view";
    public const string FloorView = "floor.view";
    public const string InventoryView = "inventory.view";
    public const string EquipmentView = "equipment.view";
    public const string MaterialsView = "materials.view";
    public const string ProcessesView = "processes.view";
    public const string UnitsView = "units.view";
    public const string ReasonsView = "reasons.view";
    public const string ShiftsView = "shifts.view";
    public const string LaborView = "labor.view";

    /// <summary>Every view permission (D-039).</summary>
    public static IReadOnlyList<string> Views { get; } =
        [OverviewView, WorkOrdersView, ScheduleView, FloorView, InventoryView, EquipmentView, MaterialsView, ProcessesView, UnitsView, ReasonsView, ShiftsView, LaborView];
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
    public const string Shifts = "shifts.manage";
    public const string LaborRecordOwn = "labor.record_own";
    public const string LaborRecord = "labor.record";
    public const string LaborApprove = "labor.approve";
    public const string People = "people.manage";
    public const string Security = "security.manage";
}
