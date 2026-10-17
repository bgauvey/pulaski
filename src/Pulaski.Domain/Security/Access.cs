namespace Pulaski.Domain.Security;

/// <summary>
/// What a user may change (D-037). Permissions are defined in code; <c>sec.permission</c> lists them for
/// reporting. Every signed-in user with a role may view everything; permissions guard changes.
/// </summary>
public static class Permissions
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

    /// <summary>Every permission with what it allows, as <c>sec.permission</c> holds them.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SchedulePlan] = "Plan jobs: create, sequence, pin, assign to lines and cancel them",
        [WorkOrderManage] = "Create work orders, dispatch, un-dispatch and firm them",
        [WorkOrderClose] = "Cancel and close work orders",
        [RateModify] = "Set and clear the modified planning rate",
        [JobExecute] = "Start, pause, resume and complete jobs",
        [JobReopen] = "Reopen a completed job",
        [ProductionReport] = "Report production and consumption into a job's runs",
        [LotReceive] = "Receive lots",
        [LotMove] = "Move lots between locations",
        [LotShip] = "Ship lots",
        [LotHold] = "Put lots on hold or in quarantine and release them",
        [InventoryCorrect] = "Correct inventory: write up, write off, correct, reverse, split and merge lots",
        [StockCount] = "Start, enter, post and cancel stock counts",
        [PartialBatchDecide] = "Approve or reject partial batches (not one's own)",
        [Materials] = "Maintain materials, classes, conversions, batching and shelf life; import materials",
        [Processes] = "Maintain processes, BOMs and capabilities",
        [Units] = "Maintain units of measure",
        [Reasons] = "Maintain reason codes",
        [Equipment] = "Maintain equipment: sites, lines, locations",
        [SiteSettings] = "Set a site's changeover mode and reporting tolerances",
        [Security] = "Maintain users and their roles",
    };
}

/// <summary>A role and what it may do (D-037). Every role but the administrator is held per site.</summary>
public sealed record Role(string Code, string Name, bool IsGlobal, IReadOnlyList<string> Permissions, string Description);

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

    public static IReadOnlyList<Role> All { get; } =
    [
        new(Administrator, "Administrator", IsGlobal: true, [.. Permissions.All.Keys], "Everything, everywhere, including users and roles."),
        new(Planner, "Planner", false,
            [Permissions.SchedulePlan, Permissions.WorkOrderManage, Permissions.WorkOrderClose, Permissions.RateModify, Permissions.Materials],
            "Plans and sequences jobs, manages work orders, planning rates and materials. Views the floor and inventory."),
        new(Supervisor, "Supervisor", false,
            [Permissions.JobExecute, Permissions.JobReopen, Permissions.ProductionReport, Permissions.LotReceive, Permissions.LotMove,
             Permissions.LotShip, Permissions.LotHold, Permissions.StockCount, Permissions.PartialBatchDecide],
            "Runs the floor: jobs, reports, receipts, moves, holds and counts; approves partial batches. Doesn't correct inventory."),
        new(Operator, "Operator", false,
            [Permissions.JobExecute, Permissions.ProductionReport, Permissions.LotReceive, Permissions.LotMove],
            "Starts, pauses and completes jobs, reports production and consumption, receives and moves lots."),
        new(Quality, "Quality", false,
            [Permissions.LotHold, Permissions.InventoryCorrect, Permissions.PartialBatchDecide],
            "Holds and releases lots, corrects inventory and decides on partial batches."),
        new(Manager, "Manager", false,
            [Permissions.JobReopen, Permissions.WorkOrderClose, Permissions.Reasons, Permissions.SiteSettings],
            "Views everything; reopens jobs, closes work orders, maintains reason codes and the site's settings."),
        new(Auditor, "Auditor", false,
            [Permissions.InventoryCorrect, Permissions.StockCount],
            "Views everything and corrects inventory; counts stock. Doesn't plan or run jobs."),
        new(ProcessEngineer, "Process engineer", false,
            [Permissions.Processes],
            "Maintains processes, BOMs and capabilities."),
    ];

    public static Role? Find(string code) => All.FirstOrDefault(r => r.Code == code);
}

/// <summary>One of a user's roles, at a site or everywhere (null site, the administrator).</summary>
public sealed record RoleGrant(Guid AssignmentId, string RoleCode, Guid? SiteId);

/// <summary>
/// What a signed-in user may do (D-037): the union of their roles. A role held at a site allows its
/// permissions there; the administrator's allows everything everywhere.
/// </summary>
public sealed record UserAccess(Guid UserId, string Username, string DisplayName, string ActorId, bool IsActive, IReadOnlyList<RoleGrant> Grants, bool MustChangePassword = false)
{
    /// <summary>A user without a role can't use Pulaski: every user must belong to one.</summary>
    public bool HasAnyRole => Grants.Count > 0;

    public bool IsAdministrator => Grants.Any(g => g.RoleCode == Roles.Administrator);

    /// <summary>Whether the user holds <paramref name="permission"/> at <paramref name="siteId"/>; for something that belongs to no site (null), at any site.</summary>
    public bool Can(string permission, Guid? siteId)
    {
        if (!IsActive)
        {
            return false;
        }

        foreach (var grant in Grants)
        {
            if (Roles.Find(grant.RoleCode) is not { } role || !role.Permissions.Contains(permission))
            {
                continue;
            }

            if (role.IsGlobal || siteId is null || grant.SiteId == siteId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The sites where the user holds the permission, or null for everywhere.</summary>
    public IReadOnlyList<Guid>? SitesFor(string permission) =>
        IsAdministrator ? null : [.. Grants.Where(g => Roles.Find(g.RoleCode)?.Permissions.Contains(permission) == true && g.SiteId is not null).Select(g => g.SiteId!.Value).Distinct()];
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
