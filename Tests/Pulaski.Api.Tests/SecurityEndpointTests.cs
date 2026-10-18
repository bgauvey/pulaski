using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Tests;

/// <summary>Signing in and permission checks on the API (D-037).</summary>
public sealed class SecurityEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly StartJobRequest Start = new() { Version = 1 };

    [Fact]
    public async Task Signed_out_callers_get_401_except_for_signing_in()
    {
        var client = factory.CreateSignedOutClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("/api/equipment", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest { Username = "admin", Password = "wrong-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest { Username = "ADMIN ", Password = ApiFactory.Password })).StatusCode);
    }

    [Fact]
    public async Task A_garbage_token_is_401()
    {
        var client = factory.CreateSignedOutClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("/api/me", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Me_lists_the_roles_and_permissions_at_their_site()
    {
        var site = Guid.CreateVersion7();
        var client = await factory.CreateClientAsAsync(Roles.Operator, site);

        var me = await client.GetFromJsonAsync<MeDto>("/api/me");

        Assert.NotNull(me);
        Assert.Equal(Roles.Operator, Assert.Single(me.Roles).RoleCode);
        var execute = Assert.Single(me.Permissions, p => p.Code == Permissions.JobExecute);
        Assert.Equal([site], execute.SiteIds);
        Assert.DoesNotContain(me.Permissions, p => p.Code == Permissions.InventoryCorrect);
    }

    [Fact]
    public async Task Viewing_is_open_to_every_role_but_changes_need_the_permission()
    {
        var job = Guid.CreateVersion7();
        var site = Guid.CreateVersion7();
        factory.Stores.Security.SetSite(SiteOf.Job, job, site);
        var planner = await factory.CreateClientAsAsync(Roles.Planner, site);

        Assert.Equal(HttpStatusCode.OK, (await planner.GetAsync(new Uri("/api/equipment", UriKind.Relative))).StatusCode);
        var refused = await planner.PostAsJsonAsync($"/api/jobs/{job}/start", Start);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_role_counts_only_at_its_own_site()
    {
        var job = Guid.CreateVersion7();
        var here = Guid.CreateVersion7();
        factory.Stores.Security.SetSite(SiteOf.Job, job, here);

        var elsewhere = await factory.CreateClientAsAsync(Roles.Supervisor, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.PostAsJsonAsync($"/api/jobs/{job}/start", Start)).StatusCode);

        // Allowed here: the request reaches the handler, which doesn't know the job.
        var local = await factory.CreateClientAsAsync(Roles.Supervisor, here);
        Assert.Equal(HttpStatusCode.NotFound, (await local.PostAsJsonAsync($"/api/jobs/{job}/start", Start)).StatusCode);
    }

    [Fact]
    public async Task Only_administrators_manage_users()
    {
        var manager = await factory.CreateClientAsAsync(Roles.Manager, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(new Uri("/api/users", UriKind.Relative))).StatusCode);

        var admin = factory.CreateClient();
        var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users");
        Assert.Contains(users!, u => u.Username == "admin");
    }

    [Fact]
    public async Task A_temporary_password_must_be_changed_before_anything_else()
    {
        var site = Guid.CreateVersion7();
        factory.Stores.Security.AddSite(site);
        var admin = factory.CreateClient();
        var created = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = "new.quality",
            DisplayName = "New Quality",
            TemporaryPassword = "Temporary-pass-1",
            RoleCode = Roles.Quality,
            SiteId = site,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var client = factory.CreateSignedOutClient();
        var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest { Username = "new.quality", Password = "Temporary-pass-1" });
        var session = await signIn.Content.ReadFromJsonAsync<SignInResponse>();
        Assert.True(session!.User.MustChangePassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri("/api/equipment", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/me", UriKind.Relative))).StatusCode);

        var weak = await client.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest { CurrentPassword = "Temporary-pass-1", NewPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var changed = await client.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest { CurrentPassword = "Temporary-pass-1", NewPassword = "Better-password-2" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/equipment", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task The_last_role_of_a_user_cant_be_taken_away()
    {
        var site = Guid.CreateVersion7();
        var user = factory.Stores.Security.AddUser("only.role", ApiFactory.Password, Roles.Auditor, site);
        var assignment = Assert.Single(factory.Stores.Security.Assignments(user.Id));

        var response = await factory.CreateClient().PostAsJsonAsync($"/api/users/roles/{assignment.Id}/end", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_user()
    {
        factory.Stores.Security.AddUser("forgetful", ApiFactory.Password, Roles.Operator, Guid.CreateVersion7());
        var client = factory.CreateSignedOutClient();
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest { Username = "forgetful", Password = "Wrong-password-" + i });
        }

        var right = await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest { Username = "forgetful", Password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
    }

    [Fact]
    public async Task Users_and_versions_round_trip()
    {
        var admin = factory.CreateClient();
        var user = factory.Stores.Security.AddUser("renamed", ApiFactory.Password, Roles.Planner, Guid.CreateVersion7());
        var before = await admin.GetFromJsonAsync<UserDto>($"/api/users/{user.Id}");

        var updated = await admin.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest { Version = before!.Version, DisplayName = "Renamed User" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.NotEqual(before.Version, (await updated.Content.ReadFromJsonAsync<VersionResponse>())!.Version);

        var stale = await admin.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest { Version = before.Version, DisplayName = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public void The_development_bootstrap_password_meets_the_password_rules()
    {
        // The API won't start in development if the first administrator's password breaks the rules.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pulaski.Api", "appsettings.Development.json");
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var admin = json.RootElement.GetProperty("Security").GetProperty("BootstrapAdmin");

        Assert.Null(PasswordRules.Problem(admin.GetProperty("Password").GetString()!, admin.GetProperty("Username").GetString()!));
    }

    [Fact]
    public async Task Administrators_maintain_roles_and_everyone_can_read_them()
    {
        var planner = await factory.CreateClientAsAsync(Roles.Planner, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.OK, (await planner.GetAsync(new Uri("/api/roles", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await planner.GetAsync(new Uri("/api/permissions", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync("/api/roles", new CreateRoleRequest { Code = "x_role", Name = "X" })).StatusCode);

        var admin = factory.CreateClient();
        var created = await admin.PostAsJsonAsync("/api/roles", new CreateRoleRequest { Code = "packer_lead", Name = "Packer lead", Permissions = [Permissions.LotMove] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var toggled = await admin.PutAsJsonAsync($"/api/roles/packer_lead/permissions/{Permissions.LotShip}", new SetRolePermissionRequest { Granted = true });
        Assert.Equal(HttpStatusCode.OK, toggled.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PutAsJsonAsync($"/api/roles/administrator/permissions/{Permissions.LotShip}", new SetRolePermissionRequest { Granted = false })).StatusCode);

        var roles = await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        Assert.Equal([Permissions.LotMove, Permissions.LotShip], Assert.Single(roles!, r => r.Code == "packer_lead").Permissions);
        var permissions = await admin.GetFromJsonAsync<List<PermissionDto>>("/api/permissions");
        Assert.Equal(Permissions.Catalog.Count, permissions!.Count);
    }
}
