namespace Pulaski.Domain.Security;

/// <summary>
/// What a user may see and change (D-037, D-039). Permissions are constants in code, the only place that checks them;
/// the API copies the catalog into <c>sec.permission</c> at start. Roles, in the database, bundle them. View permissions
/// decide which screens and data a user sees; change permissions what they may change, and bring the views they need.
/// </summary>
public static class Permissions
{
    // Viewing (D-039): which screens, and their data, a user may see at all.
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
    public const string MachinesView = "machines.view";
    public const string SiteSettingsView = "site_settings.view";

    // Changing (D-037).
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
    public const string LotRecords = "lot_records.manage";
    public const string InventoryBackdate = "inventory.backdate";
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
    public const string DowntimeCode = "downtime.code";
    public const string People = "people.manage";
    public const string Terminals = "terminals.manage";
    public const string LotAttributes = "lot_attributes.manage";
    public const string Security = "security.manage";

    // Machine data (D-050): what a Pulaski Edge node's service user holds.
    public const string EdgeIngest = "edge.ingest";

    /// <summary>Every permission, in the order screens list them, with its area and what it allows.</summary>
    public static IReadOnlyList<PermissionInfo> Catalog { get; } =
    [
        new(OverviewView, "Viewing", "See the Overview"),
        new(WorkOrdersView, "Viewing", "See work orders and their progress"),
        new(ScheduleView, "Viewing", "See the schedule"),
        new(FloorView, "Viewing", "See the floor and job pages: runs, reports and partial batches"),
        new(InventoryView, "Viewing", "See inventory: stock, lots, movements, counts, shortages and reports"),
        new(EquipmentView, "Viewing", "See the equipment hierarchy and its settings"),
        new(MaterialsView, "Viewing", "See materials, their classes, capabilities and rates"),
        new(ProcessesView, "Viewing", "See processes and BOMs"),
        new(UnitsView, "Viewing", "See units and material conversions"),
        new(ReasonsView, "Viewing", "See reason codes"),
        new(ShiftsView, "Viewing", "See shift schedules"),
        new(LaborView, "Viewing", "See labor: who is on, shift review and labor reports"),
        new(MachinesView, "Viewing", "See machines: their live state and their stops"),
        new(SiteSettingsView, "Viewing", "See a site's settings: changeover mode, reporting tolerances and calendar exceptions"),
        new(SchedulePlan, "Planning", "Plan jobs: create, sequence, pin, assign to lines and cancel them"),
        new(WorkOrderManage, "Planning", "Create work orders, dispatch, un-dispatch and firm them"),
        new(WorkOrderClose, "Planning", "Cancel and close work orders"),
        new(RateModify, "Planning", "Set and clear the modified planning rate"),
        new(JobExecute, "Floor", "Start, pause, resume and complete jobs"),
        new(JobReopen, "Floor", "Reopen a completed job"),
        new(ProductionReport, "Floor", "Report production and consumption into a job's runs"),
        new(PartialBatchDecide, "Floor", "Approve or reject partial batches (not one's own)"),
        new(LaborRecordOwn, "Labor", "Clock yourself on and off lines and jobs"),
        new(LaborRecord, "Labor", "Record labor for others and check crews onto lines"),
        new(LaborApprove, "Labor", "Approve a shift's labor and correct approved labor"),
        new(DowntimeCode, "Floor", "Code machine stops: confirm or change their reason"),
        new(LotReceive, "Inventory", "Receive lots"),
        new(LotMove, "Inventory", "Move lots between locations"),
        new(LotShip, "Inventory", "Ship lots"),
        new(LotHold, "Inventory", "Put lots on hold or in quarantine and release them"),
        new(InventoryCorrect, "Inventory", "Correct inventory: write up, write off, correct, reverse, split and merge lots"),
        new(StockCount, "Inventory", "Start, enter, post and cancel stock counts"),
        new(LotRecords, "Inventory", "Record lot attributes and attach or withdraw certificates"),
        new(InventoryBackdate, "Inventory", "Back-date receipts, consumption, production, write-ups and write-offs by up to 31 days"),
        new(Materials, "Configuration", "Maintain materials, classes, conversions, batching and shelf life; import materials"),
        new(Processes, "Configuration", "Maintain processes, BOMs and capabilities"),
        new(Units, "Configuration", "Maintain units of measure"),
        new(Reasons, "Configuration", "Maintain reason codes"),
        new(Equipment, "Configuration", "Maintain equipment: sites, lines, locations, machine profiles, edge nodes and tags"),
        new(SiteSettings, "Configuration", "Set a site's changeover mode, reporting tolerances and calendar exceptions"),
        new(Shifts, "Configuration", "Maintain shift schedules and choose which one equipment follows"),
        new(People, "Configuration", "Maintain people and crews"),
        new(Terminals, "Configuration", "Register and enrol shop-floor terminals"),
        new(LotAttributes, "Configuration", "Maintain lot attribute definitions"),
        new(Security, "Administration", "Maintain users, roles and what each role may do"),
        new(EdgeIngest, "Machine data", "Send machine data as a Pulaski Edge node: read its configuration and forward what it collects"),
    ];

    /// <summary>Every permission with what it allows, as <c>sec.permission</c> holds them.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = Catalog.ToDictionary(p => p.Code, p => p.Description, StringComparer.Ordinal);

    public static bool Exists(string code) => All.ContainsKey(code);

    /// <summary>Every view permission (D-039).</summary>
    public static IReadOnlyList<string> Views { get; } = [.. Catalog.Where(p => p.Area == "Viewing").Select(p => p.Code)];

    /// <summary>
    /// The views a change permission brings with it (D-039): whoever may change something may see where it is changed,
    /// so a role needs no separate tick to see its own screens.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Implied { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [SchedulePlan] = [ScheduleView, WorkOrdersView],
        [WorkOrderManage] = [WorkOrdersView],
        [WorkOrderClose] = [WorkOrdersView],
        [RateModify] = [MaterialsView],
        [JobExecute] = [FloorView],
        [JobReopen] = [FloorView],
        [ProductionReport] = [FloorView],
        [PartialBatchDecide] = [FloorView],
        [LotReceive] = [InventoryView],
        [LotMove] = [InventoryView],
        [LotShip] = [InventoryView],
        [LotHold] = [InventoryView],
        [InventoryCorrect] = [InventoryView],
        [StockCount] = [InventoryView],
        [LotRecords] = [InventoryView],
        [InventoryBackdate] = [InventoryView],
        // Attribute definitions sit beside the material classes they apply to.
        [LotAttributes] = [InventoryView, MaterialsView],
        [Materials] = [MaterialsView],
        // Capabilities are changed on the material page; which shifts equipment follows on the equipment page.
        [Processes] = [ProcessesView, MaterialsView],
        [Units] = [UnitsView],
        [Reasons] = [ReasonsView],
        [Equipment] = [EquipmentView],
        [Shifts] = [ShiftsView, EquipmentView],
        [SiteSettings] = [SiteSettingsView],
        // Clocking on is done from the floor; recording, approving and maintaining people from the Labor screens.
        [LaborRecordOwn] = [FloorView],
        [LaborRecord] = [LaborView, FloorView],
        [LaborApprove] = [LaborView],
        [People] = [LaborView],
        [DowntimeCode] = [MachinesView],
        // Terminals are placed on lines; their screen lists the site's equipment.
        [Terminals] = [EquipmentView],
    };
}

/// <summary>A permission the code checks, grouped by the part of Pulaski it belongs to.</summary>
public sealed record PermissionInfo(string Code, string Area, string Description);

/// <summary>A role and what it may do (D-037). Every role but the administrator is held per site.</summary>
public sealed record Role(string Code, string Name, bool IsGlobal, IReadOnlyList<string> Permissions, string Description);

/// <summary>
/// Roles live in the database (D-037): an administrator creates them and chooses their permissions. The code knows
/// only the administrator, which holds every permission everywhere and can't be changed, and the roles a new
/// database starts with.
/// </summary>
public static class Roles
{
    public const string Administrator = "administrator";
    public const string Planner = "planner";
    public const string Supervisor = "supervisor";
    public const string Operator = "operator";
    public const string Quality = "quality";
    public const string Manager = "manager";
    public const string Auditor = "auditor";
    public const string ProcessEngineer = "process_engineer";
    public const string EdgeNode = "edge_node";

    public const int MaxCodeLength = 50;

    /// <summary>The roles a new database starts with; after that they are the administrator's to change.</summary>
    public static IReadOnlyList<Role> Defaults { get; } =
    [
        new(Administrator, "Administrator", IsGlobal: true, [.. Permissions.All.Keys], "Everything, everywhere, including users and roles."),
        new(Planner, "Planner", false,
            [Permissions.OverviewView, Permissions.WorkOrdersView, Permissions.ScheduleView, Permissions.FloorView, Permissions.InventoryView,
             Permissions.MaterialsView, Permissions.ProcessesView, Permissions.MachinesView, Permissions.SiteSettingsView,
             Permissions.SchedulePlan, Permissions.WorkOrderManage, Permissions.WorkOrderClose, Permissions.RateModify, Permissions.Materials],
            "Plans and sequences jobs, manages work orders, planning rates and materials. Sees the floor, inventory and processes."),
        new(Supervisor, "Supervisor", false,
            [Permissions.OverviewView, Permissions.FloorView, Permissions.InventoryView, Permissions.WorkOrdersView, Permissions.SiteSettingsView,
             Permissions.JobExecute, Permissions.JobReopen, Permissions.ProductionReport, Permissions.LotReceive, Permissions.LotMove,
             Permissions.LotShip, Permissions.LotHold, Permissions.StockCount, Permissions.PartialBatchDecide,
             Permissions.LaborView, Permissions.LaborRecord, Permissions.LaborApprove, Permissions.MachinesView, Permissions.DowntimeCode],
            "Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches; records and approves labor; codes machine stops. Doesn't correct inventory."),
        new(Operator, "Operator", false,
            [Permissions.OverviewView, Permissions.FloorView, Permissions.InventoryView, Permissions.WorkOrdersView,
             Permissions.JobExecute, Permissions.ProductionReport, Permissions.LotReceive, Permissions.LotMove, Permissions.LaborRecordOwn,
             Permissions.MachinesView, Permissions.DowntimeCode],
            "Starts, pauses and completes jobs, reports production and consumption, receives and moves lots, clocks on and off, codes machine stops."),
        new(Quality, "Quality", false,
            [Permissions.OverviewView, Permissions.FloorView, Permissions.InventoryView, Permissions.MaterialsView, Permissions.MachinesView,
             Permissions.LotHold, Permissions.InventoryCorrect, Permissions.PartialBatchDecide, Permissions.LotRecords, Permissions.LotAttributes,
             Permissions.InventoryBackdate],
            "Holds and releases lots, corrects inventory, decides on partial batches, and keeps lot attributes and certificates."),
        new(Manager, "Manager", false,
            [.. Permissions.Views, Permissions.JobReopen, Permissions.WorkOrderClose, Permissions.Reasons, Permissions.SiteSettings, Permissions.Shifts,
             Permissions.People, Permissions.Terminals, Permissions.LaborApprove, Permissions.LotAttributes],
            "Views everything; reopens jobs, closes work orders, approves labor, maintains reason codes, shift schedules, people, lot attributes and the site's settings."),
        new(Auditor, "Auditor", false,
            [.. Permissions.Views, Permissions.InventoryCorrect, Permissions.StockCount, Permissions.InventoryBackdate],
            "Views everything and corrects inventory; counts stock. Doesn't plan or run jobs."),
        new(ProcessEngineer, "Process engineer", false,
            [Permissions.EquipmentView, Permissions.MaterialsView, Permissions.ProcessesView, Permissions.UnitsView, Permissions.Processes],
            "Maintains processes, BOMs and capabilities. Sees equipment, materials and units."),
        new(EdgeNode, "Edge node", false, [Permissions.EdgeIngest],
            "For the service user a Pulaski Edge node signs in as: reads its configuration and forwards machine data. Sees no screens."),
    ];

    /// <summary>Only the administrator is held everywhere; every other role is held at a site.</summary>
    public static bool IsGlobal(string code) => code == Administrator;

    /// <returns>Why a new role's code isn't acceptable, or null: lower case letters, digits and _, starting with a letter.</returns>
    public static string? CodeProblem(string code) =>
        code.Length is < 2 or > MaxCodeLength
            ? $"Use 2 to {MaxCodeLength} characters."
            : !char.IsAsciiLetterLower(code[0]) || !code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
                ? "Lower-case letters, digits and _ only, starting with a letter."
                : null;
}

/// <summary>One of a user's roles, at a site or everywhere (null site, the administrator), with what the role allows now.</summary>
public sealed record RoleGrant(Guid AssignmentId, string RoleCode, string RoleName, Guid? SiteId, IReadOnlyCollection<string> Permissions)
{
    public bool IsGlobal => Roles.IsGlobal(RoleCode);

    /// <summary>The role holds the permission, or one that brings it with it (a change brings its views, D-039).</summary>
    public bool Allows(string permission) =>
        IsGlobal || Permissions.Contains(permission)
        || Permissions.Any(p => Security.Permissions.Implied.TryGetValue(p, out var views) && views.Contains(permission));
}

/// <summary>
/// What a signed-in user may do (D-037): the union of their roles. A role held at a site allows its
/// permissions there; the administrator's allows everything everywhere.
/// </summary>
public sealed record UserAccess(Guid UserId, string Username, string DisplayName, string ActorId, bool IsActive, IReadOnlyList<RoleGrant> Grants, bool MustChangePassword = false)
{
    /// <summary>A user without a role can't use Pulaski: every user must belong to one.</summary>
    public bool HasAnyRole => Grants.Count > 0;

    public bool IsAdministrator => Grants.Any(g => g.IsGlobal);

    /// <summary>Whether the user holds <paramref name="permission"/> at <paramref name="siteId"/>; for something that belongs to no site (null), at any site.</summary>
    public bool Can(string permission, Guid? siteId)
    {
        if (!IsActive)
        {
            return false;
        }

        return Grants.Any(g => g.Allows(permission) && (g.IsGlobal || siteId is null || g.SiteId == siteId));
    }

    /// <summary>Whether the user holds any of <paramref name="permissions"/> at <paramref name="siteId"/> (without a site, at any site).</summary>
    public bool CanAny(IEnumerable<string> permissions, Guid? siteId) => permissions.Any(p => Can(p, siteId));

    /// <summary>Whether the user may see any screen at all (D-039); without one Pulaski has nothing to show them.</summary>
    public bool CanViewAnything => IsActive && (IsAdministrator || Grants.Any(g => Security.Permissions.Views.Any(g.Allows)) || Can(Security.Permissions.Security, null));

    /// <summary>The sites where the user holds any of <paramref name="permissions"/>, or null for everywhere (D-039).</summary>
    public IReadOnlyList<Guid>? SitesForAny(IEnumerable<string> permissions)
    {
        if (IsAdministrator)
        {
            return null;
        }

        var wanted = permissions.ToList();
        return [.. Grants.Where(g => g.SiteId is not null && wanted.Any(g.Allows)).Select(g => g.SiteId!.Value).Distinct()];
    }

    /// <summary>The sites where the user holds the permission, or null for everywhere.</summary>
    public IReadOnlyList<Guid>? SitesFor(string permission) =>
        IsAdministrator ? null : [.. Grants.Where(g => g.Allows(permission) && g.SiteId is not null).Select(g => g.SiteId!.Value).Distinct()];
}

/// <summary>The rules for a local password (D-037): at least 10 characters, not just one kind.</summary>
public static class PasswordRules
{
    public const int MinLength = 10;

    /// <returns>Why the password isn't acceptable, or null.</returns>
    public static string? Problem(string password, string username)
    {
        if (password.Length < MinLength)
        {
            return $"Use at least {MinLength} characters.";
        }

        if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
        {
            return "Don't use the user name in the password.";
        }

        var kinds = new[] { password.Any(char.IsLower), password.Any(char.IsUpper), password.Any(char.IsDigit), password.Any(c => !char.IsLetterOrDigit(c)) }.Count(k => k);
        return kinds < 2 ? "Mix at least two of: lower case, upper case, digits, symbols." : null;
    }
}
