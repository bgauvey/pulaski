using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Security;
using Pulaski.Domain;
using Pulaski.Domain.Security;
using Pulaski.Infrastructure.Security;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Users, local sign-in and roles end to end (D-037): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class SecurityTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6];

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    private async Task<Guid> SiteAsync()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "SENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "SSITE-" + _s, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        return site.Id;
    }

    [Fact]
    public async Task A_new_database_starts_with_the_default_roles_and_every_permission()
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var grants = (await connection.QueryAsync<(string Role, string Permission)>(
            "SELECT r.code, rp.permission_code FROM sec.role r JOIN sec.role_permission rp ON rp.role_id = r.id AND rp.is_granted WHERE r.is_system")).ToHashSet();
        var expected = Roles.Defaults.Where(r => !r.IsGlobal).SelectMany(r => r.Permissions.Select(p => (r.Code, p))).ToHashSet();
        Assert.Equal(expected, grants);
        Assert.Equal(Permissions.All.Count, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sec.permission"));
    }

    [Fact]
    public async Task A_local_user_signs_in_holds_their_site_role_and_changes_their_password()
    {
        var site = await SiteAsync();
        CreatedResponse created = await db.SendAsync(new CreateUserCommand("Op." + _s, "Operator " + _s, null, "Temporary-pass-1", Roles.Operator, site));

        await Assert.ThrowsAsync<UnauthorizedException>(() => db.SendAsync(new SignInCommand("op." + _s, "Wrong-pass-123")));
        Assert.True((await QueryAsync<GetUserQuery, UserDto>(new GetUserQuery(created.Id))).MustChangePassword);
        var signedIn = await db.SendAsync(new SignInCommand("OP." + _s, "Temporary-pass-1"));
        Assert.Equal((created.Id, Issuers.Local, "op." + _s, true), (signedIn.UserId, signedIn.Issuer, signedIn.Subject, signedIn.MustChangePassword));

        await using var scope = db.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecurityReadStore>();
        var access = await store.GetAccessAsync(Issuers.Local, "op." + _s, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(access);
        Assert.True(access.MustChangePassword);
        Assert.True(access.Can(Permissions.JobExecute, site));
        Assert.False(access.Can(Permissions.JobExecute, Guid.CreateVersion7()));

        await db.SendAsync(new ChangePasswordCommand(created.Id, "Temporary-pass-1", "Better-pass-22"));
        Assert.False((await db.SendAsync(new SignInCommand("op." + _s, "Better-pass-22"))).MustChangePassword);

        var user = await QueryAsync<GetUserQuery, UserDto>(new GetUserQuery(created.Id));
        Assert.Equal(("op." + _s, false, false), (user.Username, user.MustChangePassword, user.IsLocked));
        Assert.Equal(("operator", (Guid?)site, "SSITE-" + _s), (Assert.Single(user.Roles).RoleCode, user.Roles[0].SiteId, user.Roles[0].SiteCode));
        Assert.NotNull(user.LastLoginAt);
    }

    [Fact]
    public async Task Roles_are_ended_not_deleted_and_the_lookup_finds_sites()
    {
        var site = await SiteAsync();
        CreatedResponse created = await db.SendAsync(new CreateUserCommand("qa." + _s, "Quality " + _s, "qa@example.test", "Temporary-pass-1", Roles.Quality, site));
        CreatedResponse auditor = await db.SendAsync(new AssignRoleCommand(created.Id, Roles.Auditor, site));
        var first = (await QueryAsync<GetUserQuery, UserDto>(new GetUserQuery(created.Id))).Roles.Single(r => r.RoleCode == Roles.Quality);

        await db.SendAsync(new EndRoleCommand(first.AssignmentId));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new EndRoleCommand(auditor.Id)));

        var after = await QueryAsync<GetUserQuery, UserDto>(new GetUserQuery(created.Id));
        Assert.Equal(Roles.Auditor, Assert.Single(after.Roles).RoleCode);
        Assert.Equal(2, await (await db.AdminDataSource.OpenConnectionAsync()).ExecuteScalarAsync<int>(
            "SELECT count(*) FROM sec.role_assignment WHERE app_user_id = @Id", new { created.Id }));

        await using var scope = db.Services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<ISiteLookup>();
        Assert.Equal(site, await lookup.SiteOfAsync(SiteOf.Equipment, site, CancellationToken.None));
        Assert.Null(await lookup.SiteOfAsync(SiteOf.Job, Guid.CreateVersion7(), CancellationToken.None));
    }

    [Fact]
    public void Passwords_hash_with_a_salt_and_verify()
    {
        var hasher = new PasswordHasher();
        var a = hasher.Hash("Correct-horse-1");
        var b = hasher.Hash("Correct-horse-1");

        Assert.NotEqual(a, b);
        Assert.StartsWith("pbkdf2-sha256$210000$", a, StringComparison.Ordinal);
        Assert.True(hasher.Verify("Correct-horse-1", a));
        Assert.False(hasher.Verify("Correct-horse-2", a));
        Assert.False(hasher.Verify("Correct-horse-1", "garbage"));
    }

    [Fact]
    public async Task A_role_made_on_screen_gives_its_holders_what_it_allows_and_keeps_taken_permissions_as_rows()
    {
        var site = await SiteAsync();
        var code = "lead_" + _s;
        await db.SendAsync(new CreateRoleCommand(code, "Lead " + _s, "Test role", [Permissions.LotHold]));
        CreatedResponse user = await db.SendAsync(new CreateUserCommand("lead." + _s, "Lead", null, "Temporary-pass-1", code, site));

        await using var scope = db.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecurityReadStore>();
        async Task<UserAccess> AccessAsync() => (await store.GetAccessAsync(Issuers.Local, "lead." + _s, DateTimeOffset.UtcNow, CancellationToken.None))!;
        Assert.True((await AccessAsync()).Can(Permissions.LotHold, site));
        Assert.Equal("Lead " + _s, Assert.Single((await AccessAsync()).Grants).RoleName);

        await db.SendAsync(new SetRolePermissionCommand(code, Permissions.LotShip, true));
        await db.SendAsync(new SetRolePermissionCommand(code, Permissions.LotHold, false));
        var access = await AccessAsync();
        Assert.True(access.Can(Permissions.LotShip, site));
        Assert.False(access.Can(Permissions.LotHold, site));

        var role = Assert.Single(await QueryAsync<ListRolesQuery, IReadOnlyList<RoleDto>>(new ListRolesQuery()), r => r.Code == code);
        Assert.Equal([Permissions.LotShip], role.Permissions);
        Assert.Equal(1, role.Holders);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UpdateRoleCommand(code, role.Version, role.Name, null, false, [])));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new UpdateRoleCommand(code, role.Version - 1, "Old", null, true, [])));
        Assert.Equal(2, await (await db.AdminDataSource.OpenConnectionAsync()).ExecuteScalarAsync<int>(
            "SELECT count(*) FROM sec.role_permission rp JOIN sec.role r ON r.id = rp.role_id WHERE r.code = @Code", new { Code = code }));

        var admin = Assert.Single(await QueryAsync<ListRolesQuery, IReadOnlyList<RoleDto>>(new ListRolesQuery()), r => r.Code == Roles.Administrator);
        Assert.Equal((true, Permissions.All.Count), (admin.IsLocked, admin.Permissions.Count));
        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Fact]
    public async Task The_permission_catalog_syncs_into_the_database()
    {
        await using (var connection = await db.AdminDataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE sec.permission SET description = 'old words' WHERE code = @Code", new { Code = Permissions.LotShip });
        }

        await using var scope = db.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using (var transaction = await unitOfWork.BeginAsync(CancellationToken.None))
        {
            await scope.ServiceProvider.GetRequiredService<ISecurityRepository>().SyncPermissionsAsync(Permissions.Catalog, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await using var check = await db.AdminDataSource.OpenConnectionAsync();
        Assert.Equal(Permissions.All[Permissions.LotShip], await check.ExecuteScalarAsync<string>("SELECT description FROM sec.permission WHERE code = @Code", new { Code = Permissions.LotShip }));
    }
}
