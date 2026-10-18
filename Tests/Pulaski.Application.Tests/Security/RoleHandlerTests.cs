using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Security;
using Pulaski.Domain;
using Pulaski.Domain.Security;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Security;

/// <summary>Roles an administrator maintains (D-037).</summary>
public class RoleHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly FixedRequestContext _admin = new("admin", Guid.CreateVersion7());

    private InMemorySecurity Security => _f.Stores.Security;

    private CreateRoleHandler Create() => new(_f.Stores.UnitOfWork, Security, _admin, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private UpdateRoleHandler Update() => new(_f.Stores.UnitOfWork, Security, _admin, _f.Stores.Events, _f.Events, _f.Time);

    private SetRolePermissionHandler Set() => new(_f.Stores.UnitOfWork, Security, _admin, _f.Stores.Events, _f.Events);

    private long Version(string code) => Security.FindRoleForUpdateAsync(code, CancellationToken.None).Result!.Version;

    [Fact]
    public async Task A_new_role_has_the_permissions_chosen_and_a_fixed_lower_case_code()
    {
        await Create().HandleAsync(new CreateRoleCommand(" Line_Lead ", "Line lead", "Runs one line", [Permissions.JobExecute, Permissions.LotHold, Permissions.LotHold]), CancellationToken.None);

        var role = Security.Role("line_lead")!;
        Assert.Equal(("Line lead", true), (role.Name, role.IsActive));
        Assert.Equal([Permissions.JobExecute, Permissions.LotHold], role.Permissions.Order(StringComparer.Ordinal));
        Assert.Equal(EventTypes.RoleCreated, _f.Stores.Events.Appended[^1].Type);

        await Assert.ThrowsAsync<ConflictException>(() => Create().HandleAsync(new CreateRoleCommand("line_lead", "Again", null, []), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => Create().HandleAsync(new CreateRoleCommand("line-lead", "Bad", null, []), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => Create().HandleAsync(new CreateRoleCommand("ok_code", "Bad", null, ["no.such"]), CancellationToken.None));
    }

    [Fact]
    public async Task Editing_a_role_sets_its_permissions_and_checks_the_version()
    {
        await Create().HandleAsync(new CreateRoleCommand("shift_lead", "Shift lead", null, [Permissions.JobExecute]), CancellationToken.None);
        var version = Version("shift_lead");

        await Update().HandleAsync(new UpdateRoleCommand("shift_lead", version, "Shift leader", "Nights", true, [Permissions.LotMove, Permissions.LotShip]), CancellationToken.None);

        var role = Security.Role("shift_lead")!;
        Assert.Equal(("Shift leader", "Nights"), (role.Name, role.Description));
        Assert.Equal([Permissions.LotMove, Permissions.LotShip], role.Permissions);
        await Assert.ThrowsAsync<ConflictException>(() =>
            Update().HandleAsync(new UpdateRoleCommand("shift_lead", version, "Stale", null, true, []), CancellationToken.None));
    }

    [Fact]
    public async Task The_permissions_screen_grants_and_takes_one_at_a_time()
    {
        var before = Version(Roles.Planner);
        await Set().HandleAsync(new SetRolePermissionCommand(Roles.Planner, Permissions.LotHold, true), CancellationToken.None);
        Assert.Contains(Permissions.LotHold, Security.Role(Roles.Planner)!.Permissions);
        Assert.NotEqual(before, Version(Roles.Planner));

        var events = _f.Stores.Events.Appended.Count;
        await Set().HandleAsync(new SetRolePermissionCommand(Roles.Planner, Permissions.LotHold, true), CancellationToken.None);
        Assert.Equal(events, _f.Stores.Events.Appended.Count);

        await Set().HandleAsync(new SetRolePermissionCommand(Roles.Planner, Permissions.LotHold, false), CancellationToken.None);
        Assert.DoesNotContain(Permissions.LotHold, Security.Role(Roles.Planner)!.Permissions);
        Assert.Equal(EventTypes.RolePermissionChanged, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task The_administrator_role_cant_be_changed()
    {
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Set().HandleAsync(new SetRolePermissionCommand(Roles.Administrator, Permissions.Security, false), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Update().HandleAsync(new UpdateRoleCommand(Roles.Administrator, Version(Roles.Administrator), "Admin", null, false, []), CancellationToken.None));
    }

    [Fact]
    public async Task A_role_someone_holds_cant_be_switched_off_and_a_switched_off_role_cant_be_given()
    {
        var site = Guid.CreateVersion7();
        Security.AddUser("aud", "Test-password-1", Roles.Auditor, site);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Update().HandleAsync(new UpdateRoleCommand(Roles.Auditor, Version(Roles.Auditor), "Auditor", null, false, [Permissions.InventoryCorrect]), CancellationToken.None));

        await Update().HandleAsync(new UpdateRoleCommand(Roles.Manager, Version(Roles.Manager), "Manager", null, false, []), CancellationToken.None);
        var create = new CreateUserHandler(_f.Stores.UnitOfWork, Security, new PlainPasswordHasher(), _admin, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() =>
            create.HandleAsync(new CreateUserCommand("mgr", "Mgr", null, "Temporary-pass-1", Roles.Manager, site), CancellationToken.None));
        Assert.Contains("switched off", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_a_user_may_do_follows_their_role_as_it_is_now()
    {
        var site = Guid.CreateVersion7();
        var user = Security.AddUser("op", "Test-password-1", Roles.Operator, site);
        static async Task<bool> Can(InMemorySecurity s, SecurityUser u) =>
            (await s.GetAccessAsync(u.Issuer, u.Subject, HandlerFixture.Now, CancellationToken.None))!.Can(Permissions.LotShip, null);

        Assert.False(await Can(Security, user));
        await Set().HandleAsync(new SetRolePermissionCommand(Roles.Operator, Permissions.LotShip, true), CancellationToken.None);
        Assert.True(await Can(Security, user));
    }
}
