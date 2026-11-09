using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Security;
using Pulaski.Domain;
using Pulaski.Domain.Security;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Security;

/// <summary>Local sign-in, users and role assignments (D-037).</summary>
public class SecurityHandlerTests
{
    private const string Password = "Test-password-1";
    private readonly HandlerFixture _f = new();
    private readonly PlainPasswordHasher _hasher = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly SecurityUser _admin;

    public SecurityHandlerTests()
    {
        _f.Stores.Security.AddSite(_site);
        _admin = _f.Stores.Security.AddUser("admin", Password, Roles.Administrator);
    }

    private InMemorySecurity Security => _f.Stores.Security;

    private static FixedRequestContext As(SecurityUser user) => new FixedRequestContext(user.Username!, user.Id);

    private SignInHandler SignIn() => new(_f.Stores.UnitOfWork, Security, _hasher, _f.Stores.Events, _f.Events, _f.Time);

    private CreateUserHandler Create() => new(_f.Stores.UnitOfWork, Security, _hasher, As(_admin), _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private UpdateUserHandler Update(SecurityUser actor) => new(_f.Stores.UnitOfWork, Security, As(actor), _f.Stores.Events, _f.Events, _f.Time);

    private EndRoleHandler EndRole() => new(_f.Stores.UnitOfWork, Security, As(_admin), _f.Stores.Events, _f.Events, _f.Time);

    private AssignRoleHandler Assign() => new(_f.Stores.UnitOfWork, Security, As(_admin), _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private ProviderSignInHandler ProviderSignIn() => new(_f.Stores.UnitOfWork, Security, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    [Fact]
    public async Task Someone_new_from_a_provider_is_kept_without_a_role_and_let_in_once_given_one()
    {
        var command = new ProviderSignInCommand("Contoso", "https://login.contoso.example/tenant/v2.0", "abc-123", "Ana Ng", "ana@contoso.example");

        var refused = await Assert.ThrowsAsync<UnauthorizedException>(() => ProviderSignIn().HandleAsync(command, CancellationToken.None));
        Assert.Contains("no role yet", refused.Message, StringComparison.Ordinal);
        var ana = (await Security.FindUserByIdentityForUpdateAsync(command.Issuer, command.Subject, CancellationToken.None))!.Value;
        Assert.Equal(("Ana Ng", "ana@contoso.example", (string?)null), (ana.DisplayName, ana.Email, ana.Username));
        Assert.Equal(("Contoso sign-in", EventTypes.UserCreated), (_f.Stores.Events.Appended[^1].Reason, _f.Stores.Events.Appended[^1].Type));
        Assert.True((await Security.GetUserAsync(ana.Id, HandlerFixture.Now, CancellationToken.None))!.IsAwaitingRole);

        // Signing in again doesn't add them twice; still no role.
        await Assert.ThrowsAsync<UnauthorizedException>(() => ProviderSignIn().HandleAsync(command, CancellationToken.None));
        Assert.Equal(2, Security.UserCount);

        await Assign().HandleAsync(new AssignRoleCommand(ana.Id, Roles.Operator, _site), CancellationToken.None);
        var signedIn = await ProviderSignIn().HandleAsync(command with { DisplayName = "Ana Ng-Berg" }, CancellationToken.None);
        Assert.Equal((ana.Id, command.Issuer, "abc-123", "Ana Ng-Berg", false), (signedIn.UserId, signedIn.Issuer, signedIn.Subject, signedIn.DisplayName, signedIn.MustChangePassword));
        Assert.Equal(EventTypes.SignedIn, _f.Stores.Events.Appended[^1].Type);

        // A provider can't vouch for a Pulaski password user, and a switched-off user stays out.
        await Assert.ThrowsAsync<UnauthorizedException>(() => ProviderSignIn().HandleAsync(command with { Issuer = Issuers.Local, Subject = "admin" }, CancellationToken.None));
        var current = (await Security.GetUserForUpdateAsync(ana.Id, CancellationToken.None))!;
        await Update(_admin).HandleAsync(new UpdateUserCommand(ana.Id, current.Version, "Ana Ng-Berg", "ana@contoso.example", false), CancellationToken.None);
        Assert.Contains("switched off", (await Assert.ThrowsAsync<UnauthorizedException>(() => ProviderSignIn().HandleAsync(command, CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_passwords_for_administrators_only_others_are_sent_to_the_provider_but_machines_keep_theirs()
    {
        await Create().HandleAsync(new CreateUserCommand("planner1", "P", null, "Temporary-pass-1", Roles.Planner, _site), CancellationToken.None);
        var edge = await Create().HandleAsync(new CreateUserCommand("edge1", "Edge", null, "Temporary-pass-1", Roles.EdgeNode, _site), CancellationToken.None);

        Assert.Contains("organisation's sign-in", (await Assert.ThrowsAsync<UnauthorizedException>(() =>
            SignIn().HandleAsync(new SignInCommand("planner1", "Temporary-pass-1", AdministratorsOnly: true), CancellationToken.None))).Message, StringComparison.Ordinal);
        await SignIn().HandleAsync(new SignInCommand("admin", Password, AdministratorsOnly: true), CancellationToken.None);
        Assert.True((await Security.GetUserForUpdateAsync(edge.Id, CancellationToken.None))!.Value.IsServiceAccount);
        await SignIn().HandleAsync(new SignInCommand("edge1", "Temporary-pass-1", AdministratorsOnly: true), CancellationToken.None);
        await SignIn().HandleAsync(new SignInCommand("planner1", "Temporary-pass-1"), CancellationToken.None);
    }

    [Fact]
    public async Task Only_a_role_allowed_at_every_site_is_given_without_a_site_and_it_stays_allowed_while_held_so()
    {
        var roles = new RoleHandlerTests.Handlers(_f, As(_admin));
        await roles.Create.HandleAsync(new CreateRoleCommand("regional_quality", "Regional quality", null, [Permissions.LotHold]), CancellationToken.None);
        var user = await Create().HandleAsync(new CreateUserCommand("rq1", "R Q", null, "Temporary-pass-1", Roles.Operator, _site), CancellationToken.None);

        Assert.Contains("allow it at every site", (await Assert.ThrowsAsync<DomainRuleException>(() =>
            Assign().HandleAsync(new AssignRoleCommand(user.Id, "regional_quality", null), CancellationToken.None))).Message, StringComparison.Ordinal);

        var version = (await Security.FindRoleForUpdateAsync("regional_quality", CancellationToken.None))!.Version;
        await roles.Update.HandleAsync(new UpdateRoleCommand("regional_quality", version, "Regional quality", null, true, [Permissions.LotHold], AllowsEverySite: true), CancellationToken.None);
        await Assign().HandleAsync(new AssignRoleCommand(user.Id, "regional_quality", null), CancellationToken.None);
        Assert.Contains(Security.Assignments(user.Id), a => a.RoleCode == "regional_quality" && a.SiteId is null);

        var access = (await Security.GetAccessAsync(Issuers.Local, "rq1", HandlerFixture.Now, CancellationToken.None))!;
        Assert.True(access.Can(Permissions.LotHold, Guid.CreateVersion7()));

        version = (await Security.FindRoleForUpdateAsync("regional_quality", CancellationToken.None))!.Version;
        Assert.Contains("at every site", (await Assert.ThrowsAsync<DomainRuleException>(() => roles.Update.HandleAsync(
            new UpdateRoleCommand("regional_quality", version, "Regional quality", null, true, [Permissions.LotHold], AllowsEverySite: false), CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_user_comes_with_a_role_and_a_temporary_password()
    {
        var created = await Create().HandleAsync(new CreateUserCommand(" J.Smith ", "Jo Smith", null, "Temporary-pass-1", Roles.Operator, _site), CancellationToken.None);

        var signedIn = await SignIn().HandleAsync(new SignInCommand("j.smith", "Temporary-pass-1"), CancellationToken.None);
        Assert.Equal((created.Id, "j.smith", true), (signedIn.UserId, signedIn.Username, signedIn.MustChangePassword));
        Assert.Equal((Roles.Operator, (Guid?)_site), Assert.Single(Security.Assignments(created.Id)) is var a ? (a.RoleCode, a.SiteId) : default);
        Assert.Equal(EventTypes.SignedIn, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Site_roles_need_a_site_and_the_administrator_none()
    {
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Create().HandleAsync(new CreateUserCommand("planner1", "P", null, "Temporary-pass-1", Roles.Planner, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Create().HandleAsync(new CreateUserCommand("planner1", "P", null, "Temporary-pass-1", Roles.Planner, Guid.CreateVersion7()), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Create().HandleAsync(new CreateUserCommand("admin2", "A", null, "Temporary-pass-1", Roles.Administrator, _site), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Create().HandleAsync(new CreateUserCommand("x1", "X", null, "Temporary-pass-1", "superuser", null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            Create().HandleAsync(new CreateUserCommand("weak", "W", null, "password", Roles.Planner, _site), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() =>
            Create().HandleAsync(new CreateUserCommand("ADMIN", "A", null, "Temporary-pass-1", Roles.Administrator, null), CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_passwords_count_up_to_a_lockout()
    {
        var user = Security.AddUser("op", Password, Roles.Operator, _site);
        for (var i = 1; i <= 4; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedException>(() => SignIn().HandleAsync(new SignInCommand("op", "nope"), CancellationToken.None));
            Assert.Equal(i, Security.Credential(user.Id)!.FailedAttempts);
        }

        await Assert.ThrowsAsync<UnauthorizedException>(() => SignIn().HandleAsync(new SignInCommand("op", "nope"), CancellationToken.None));
        Assert.Equal(HandlerFixture.Now.AddMinutes(15), Security.Credential(user.Id)!.LockedUntil);
        await Assert.ThrowsAsync<UnauthorizedException>(() => SignIn().HandleAsync(new SignInCommand("op", Password), CancellationToken.None));

        _f.Time.Advance(TimeSpan.FromMinutes(16));
        await SignIn().HandleAsync(new SignInCommand("op", Password), CancellationToken.None);
        Assert.Equal(0, Security.Credential(user.Id)!.FailedAttempts);
    }

    [Fact]
    public async Task Changing_a_password_checks_the_current_one_and_the_rules()
    {
        var user = Security.AddUser("qa", Password, Roles.Quality, _site, mustChange: true);
        var change = new ChangePasswordHandler(_f.Stores.UnitOfWork, Security, _hasher, _f.Stores.Events, _f.Events, _f.Time);

        await Assert.ThrowsAsync<ValidationException>(() => change.HandleAsync(new ChangePasswordCommand(user.Id, "wrong", "Another-pass-2"), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => change.HandleAsync(new ChangePasswordCommand(user.Id, Password, Password), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => change.HandleAsync(new ChangePasswordCommand(user.Id, Password, "qa-is-my-name-1"), CancellationToken.None));
        await change.HandleAsync(new ChangePasswordCommand(user.Id, Password, "Another-pass-2"), CancellationToken.None);

        Assert.False(Security.Credential(user.Id)!.MustChange);
    }

    [Fact]
    public async Task Every_user_keeps_a_role_and_someone_stays_administrator()
    {
        var user = Security.AddUser("aud", Password, Roles.Auditor, _site);
        var only = Assert.Single(Security.Assignments(user.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => EndRole().HandleAsync(new EndRoleCommand(only.Id), CancellationToken.None));

        await Assign().HandleAsync(new AssignRoleCommand(user.Id, Roles.Quality, _site), CancellationToken.None);
        await Assert.ThrowsAsync<DomainRuleException>(() => Assign().HandleAsync(new AssignRoleCommand(user.Id, Roles.Quality, _site), CancellationToken.None));
        await EndRole().HandleAsync(new EndRoleCommand(only.Id), CancellationToken.None);
        Assert.Equal(Roles.Quality, Assert.Single(Security.Assignments(user.Id), a => a.ValidTo is null).RoleCode);

        // The only administrator can't lose the role, switch themselves off or be switched off.
        await Assign().HandleAsync(new AssignRoleCommand(_admin.Id, Roles.Planner, _site), CancellationToken.None);
        var adminRole = Assert.Single(Security.Assignments(_admin.Id), a => a.RoleCode == Roles.Administrator);
        await Assert.ThrowsAsync<DomainRuleException>(() => EndRole().HandleAsync(new EndRoleCommand(adminRole.Id), CancellationToken.None));
        var version = (await Security.GetUserForUpdateAsync(_admin.Id, CancellationToken.None))!.Version;
        await Assert.ThrowsAsync<DomainRuleException>(() => Update(_admin).HandleAsync(new UpdateUserCommand(_admin.Id, version, "Admin", null, false), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => Update(user).HandleAsync(new UpdateUserCommand(_admin.Id, version, "Admin", null, false), CancellationToken.None));
    }

    [Fact]
    public async Task A_switched_off_user_or_one_without_a_role_cant_sign_in()
    {
        var user = Security.AddUser("gone", Password, Roles.Operator, _site);
        var version = (await Security.GetUserForUpdateAsync(user.Id, CancellationToken.None))!.Version;
        await Update(_admin).HandleAsync(new UpdateUserCommand(user.Id, version, "Gone", null, false), CancellationToken.None);
        await Assert.ThrowsAsync<UnauthorizedException>(() => SignIn().HandleAsync(new SignInCommand("gone", Password), CancellationToken.None));

        var roleless = Security.AddUser("roleless", Password, Roles.Operator, _site);
        await Security.EndAssignmentAsync(Assert.Single(Security.Assignments(roleless.Id)).Id, HandlerFixture.Now, null, CancellationToken.None);
        var refused = await Assert.ThrowsAsync<UnauthorizedException>(() => SignIn().HandleAsync(new SignInCommand("roleless", Password), CancellationToken.None));
        Assert.Contains("no role", refused.Message, StringComparison.Ordinal);
    }
}
