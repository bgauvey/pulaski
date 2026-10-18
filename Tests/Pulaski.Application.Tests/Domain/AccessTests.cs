using Pulaski.Domain.Security;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Roles, permissions and password rules (D-037).</summary>
public class AccessTests
{
    private static readonly Guid SiteA = Guid.CreateVersion7();
    private static readonly Guid SiteB = Guid.CreateVersion7();

    // The roles as a new database starts with them.
    private static UserAccess User(bool active = true, params (string Role, Guid? Site)[] grants) =>
        new(Guid.CreateVersion7(), "u", "U", "u", active, [.. grants.Select(g => Roles.Defaults.Single(r => r.Code == g.Role) is var role
            ? new RoleGrant(Guid.CreateVersion7(), role.Code, role.Name, g.Site, role.IsGlobal ? [] : role.Permissions)
            : null!)]);

    [Fact]
    public void A_site_role_allows_its_permissions_at_that_site_only()
    {
        var supervisor = User(true, (Roles.Supervisor, SiteA));

        Assert.True(supervisor.Can(Permissions.JobExecute, SiteA));
        Assert.False(supervisor.Can(Permissions.JobExecute, SiteB));
        Assert.False(supervisor.Can(Permissions.InventoryCorrect, SiteA));

        // Something enterprise-wide (no site): holding it at any site is enough.
        Assert.True(supervisor.Can(Permissions.JobExecute, null));
    }

    [Fact]
    public void Permissions_are_the_union_of_roles_and_the_administrator_has_all_everywhere()
    {
        var both = User(true, (Roles.Planner, SiteA), (Roles.Quality, SiteB));
        Assert.True(both.Can(Permissions.SchedulePlan, SiteA));
        Assert.True(both.Can(Permissions.InventoryCorrect, SiteB));
        Assert.False(both.Can(Permissions.InventoryCorrect, SiteA));
        Assert.Equal([SiteB], both.SitesFor(Permissions.InventoryCorrect));

        var admin = User(true, (Roles.Administrator, null));
        Assert.All(Permissions.All.Keys, p => Assert.True(admin.Can(p, SiteB)));
        Assert.Null(admin.SitesFor(Permissions.Security));
    }

    [Fact]
    public void A_switched_off_user_or_one_without_a_role_can_do_nothing()
    {
        Assert.False(User(false, (Roles.Administrator, null)).Can(Permissions.Security, null));
        var none = User(true);
        Assert.False(none.HasAnyRole);
        Assert.False(none.Can(Permissions.JobExecute, SiteA));
    }

    [Theory]
    [InlineData(Roles.Planner, Permissions.SchedulePlan, true)]
    [InlineData(Roles.Planner, Permissions.ProductionReport, false)]
    [InlineData(Roles.Planner, Permissions.InventoryCorrect, false)]
    [InlineData(Roles.Planner, Permissions.JobExecute, false)]
    [InlineData(Roles.Auditor, Permissions.InventoryCorrect, true)]
    [InlineData(Roles.Auditor, Permissions.SchedulePlan, false)]
    [InlineData(Roles.Auditor, Permissions.JobExecute, false)]
    [InlineData(Roles.Supervisor, Permissions.PartialBatchDecide, true)]
    [InlineData(Roles.Supervisor, Permissions.InventoryCorrect, false)]
    [InlineData(Roles.Quality, Permissions.PartialBatchDecide, true)]
    [InlineData(Roles.Operator, Permissions.PartialBatchDecide, false)]
    [InlineData(Roles.ProcessEngineer, Permissions.Processes, true)]
    [InlineData(Roles.ProcessEngineer, Permissions.Materials, false)]
    [InlineData(Roles.Manager, Permissions.Security, false)]
    public void Roles_hold_what_was_agreed(string role, string permission, bool expected) =>
        Assert.Equal(expected, User(true, (role, SiteA)).Can(permission, SiteA));

    [Fact]
    public void Every_default_role_permission_exists_and_only_the_administrator_is_global()
    {
        Assert.All(Roles.Defaults.SelectMany(r => r.Permissions), p => Assert.True(Permissions.Exists(p), p));
        Assert.Equal([Roles.Administrator], Roles.Defaults.Where(r => r.IsGlobal).Select(r => r.Code));
        Assert.All(Roles.Defaults, r => Assert.Equal(r.IsGlobal, Roles.IsGlobal(r.Code)));
    }

    [Fact]
    public void A_role_allows_what_it_holds_now_and_the_administrator_everything()
    {
        var custom = new UserAccess(Guid.CreateVersion7(), "u", "U", "u", true, [new RoleGrant(Guid.CreateVersion7(), "line_lead", "Line lead", SiteA, [Permissions.LotHold])]);
        Assert.True(custom.Can(Permissions.LotHold, SiteA));
        Assert.False(custom.Can(Permissions.JobExecute, SiteA));

        // The administrator's grant lists nothing: the code gives it everything, including permissions added later.
        var admin = new UserAccess(Guid.CreateVersion7(), "a", "A", "a", true, [new RoleGrant(Guid.CreateVersion7(), Roles.Administrator, "Administrator", null, [])]);
        Assert.True(admin.Can(Permissions.Security, SiteB));
    }

    [Theory]
    [InlineData("line_lead", true)]
    [InlineData("qa2", true)]
    [InlineData("Line_lead", false)]
    [InlineData("2nd_shift", false)]
    [InlineData("line-lead", false)]
    [InlineData("x", false)]
    public void Role_codes_are_lower_case_words(string code, bool ok) => Assert.Equal(ok, Roles.CodeProblem(code) is null);

    [Fact]
    public void The_catalog_lists_each_permission_once_with_an_area()
    {
        Assert.Equal(Permissions.Catalog.Count, Permissions.Catalog.Select(p => p.Code).Distinct().Count());
        Assert.All(Permissions.Catalog, p => Assert.False(string.IsNullOrWhiteSpace(p.Area)));
    }

    [Fact]
    public void The_contracts_name_the_same_permissions_for_clients()
    {
        static Dictionary<string, string> Codes(Type type) =>
            type.GetFields().Where(f => f.IsLiteral).ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        Assert.Equal(Codes(typeof(Permissions)), Codes(typeof(Pulaski.Contracts.Security.PermissionCodes)));
    }

    [Theory]
    [InlineData("short1A", "jo", false)]
    [InlineData("alllowercaseletters", "jo", false)]
    [InlineData("has-the-jsmith-name1", "jsmith", false)]
    [InlineData("Long enough words", "jo", true)]
    [InlineData("correcthorse42", "jo", true)]
    public void Passwords_need_ten_characters_of_two_kinds_without_the_user_name(string password, string username, bool ok) =>
        Assert.Equal(ok, PasswordRules.Problem(password, username) is null);
}
