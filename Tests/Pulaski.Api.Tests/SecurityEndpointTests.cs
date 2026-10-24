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
    public async Task Only_administrators_see_and_maintain_roles()
    {
        var planner = await factory.CreateClientAsAsync(Roles.Planner, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.GetAsync(new Uri("/api/roles", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.GetAsync(new Uri("/api/permissions", UriKind.Relative))).StatusCode);
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

    [Fact]
    public async Task Managers_maintain_shift_schedules_and_planners_dont_see_them()
    {
        var site = Guid.CreateVersion7();
        factory.Stores.Shifts.AddSite(site);
        factory.Stores.Security.SetSite(SiteOf.Equipment, site, site);
        var request = new Pulaski.Contracts.Shifts.CreateShiftScheduleRequest
        {
            SiteId = site,
            Code = "2X8",
            Name = "Two shifts",
            CycleStart = new DateOnly(2026, 10, 12),
            Shifts = [new Pulaski.Contracts.Shifts.ShiftDto { Code = "A", Start = "06:00", Minutes = 480, Days = [0, 1, 2, 3, 4] }],
        };

        var planner = await factory.CreateClientAsAsync(Roles.Planner, site);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync("/api/shift-schedules", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.GetAsync(new Uri($"/api/shift-schedules?siteId={site}", UriKind.Relative))).StatusCode);
        var auditor = await factory.CreateClientAsAsync(Roles.Auditor, site);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(new Uri($"/api/shift-schedules?siteId={site}", UriKind.Relative))).StatusCode);

        var manager = await factory.CreateClientAsAsync(Roles.Manager, site);
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync("/api/shift-schedules", request)).StatusCode);
        var preview = await manager.PostAsJsonAsync("/api/shift-schedules/preview", new Pulaski.Contracts.Shifts.PreviewShiftScheduleRequest
        {
            SiteId = site,
            CycleStart = request.CycleStart,
            Shifts = request.Shifts,
            From = new DateOnly(2026, 10, 19),
        });
        Assert.Equal(5, (await preview.Content.ReadFromJsonAsync<Pulaski.Contracts.Shifts.ShiftPreviewDto>())!.Shifts.Count);
    }

    [Fact]
    public async Task Site_settings_are_seen_by_auditors_and_changed_only_by_managers()
    {
        var site = factory.Stores.Equipment.AddHierarchy().Site.Id;
        factory.Stores.Security.SetSite(SiteOf.Equipment, site, site);
        var holiday = new Pulaski.Contracts.Shifts.CreateCalendarExceptionRequest
        {
            ScopeEquipmentId = site,
            Kind = "holiday",
            StartsAt = new DateTimeOffset(2026, 12, 25, 6, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 12, 26, 6, 0, 0, TimeSpan.Zero),
        };
        var tolerances = new Pulaski.Contracts.Execution.SetExecutionPolicyRequest { RateTolerancePct = 120m, ConsumptionTolerancePct = 120m };
        var changeovers = new Pulaski.Contracts.Scheduling.SetSchedulingPolicyRequest { ChangeoverMode = "scheduler" };

        // The auditor sees the settings but can't change any of them.
        var auditor = await factory.CreateClientAsAsync(Roles.Auditor, site);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(new Uri($"/api/calendar-exceptions?siteId={site}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/calendar-exceptions", holiday)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PutAsJsonAsync($"/api/execution/sites/{site}/policy", tolerances)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PutAsJsonAsync($"/api/scheduling/sites/{site}/policy", changeovers)).StatusCode);

        // An operator doesn't see them at all.
        var op = await factory.CreateClientAsAsync(Roles.Operator, site);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync(new Uri($"/api/calendar-exceptions?siteId={site}", UriKind.Relative))).StatusCode);

        var manager = await factory.CreateClientAsAsync(Roles.Manager, site);
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync("/api/calendar-exceptions", holiday)).StatusCode);
    }

    [Fact]
    public async Task Reads_need_the_view_at_the_site_and_lists_keep_to_the_callers_sites()
    {
        var here = Guid.CreateVersion7();
        var there = Guid.CreateVersion7();
        factory.Stores.Security.SetSite(SiteOf.Equipment, here, here);
        factory.Stores.Security.SetSite(SiteOf.Equipment, there, there);
        var hereOrder = factory.Stores.WorkOrders.Add("planned", version: 1, siteId: here);
        var thereOrder = factory.Stores.WorkOrders.Add("planned", version: 1, siteId: there);

        // An operator sees the floor and inventory, not the schedule or the configuration screens.
        var op = await factory.CreateClientAsAsync(Roles.Operator, here);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync(new Uri($"/api/execution/sites/{there}/board", UriKind.Relative))).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await op.GetAsync(new Uri($"/api/execution/sites/{here}/board", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync(new Uri("/api/reason-codes/all", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync(new Uri("/api/inventory/stock", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync(new Uri("/api/uoms", UriKind.Relative))).StatusCode);

        // Work orders are listed only at the operator's own site.
        var orders = await op.GetFromJsonAsync<List<Pulaski.Contracts.WorkOrders.WorkOrderDto>>("/api/work-orders");
        Assert.Contains(orders!, o => o.Id == hereOrder.Id);
        Assert.DoesNotContain(orders!, o => o.Id == thereOrder.Id);

        // Someone whose roles allow no screen at all may sign in but sees nothing.
        var nothing = Guid.CreateVersion7();
        factory.Stores.Security.AddRole("nothing", "Nothing", []);
        var blind = await factory.CreateClientAsAsync("nothing", nothing);
        Assert.Equal(HttpStatusCode.Forbidden, (await blind.GetAsync(new Uri("/api/equipment", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await blind.GetAsync(new Uri("/api/me", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Operators_clock_themselves_supervisors_record_for_others_and_planners_see_no_labor()
    {
        var site = Guid.CreateVersion7();
        factory.Stores.Labor.AddSite(site);
        var line = factory.Stores.Labor.AddLine(site, "EXT-01");
        factory.Stores.Security.SetSite(SiteOf.Equipment, line, site);
        factory.Stores.Security.SetSite(SiteOf.Equipment, site, site);
        var someone = factory.Stores.Labor.AddPerson(site, "Pat");
        var onLine = new Pulaski.Contracts.Labor.ClockOnRequest { PersonId = someone, EquipmentId = line };

        var op = await factory.CreateClientAsAsync(Roles.Operator, site);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/labor/sessions", onLine)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync(new Uri($"/api/labor/sites/{site}/report?from=2026-10-01&to=2026-10-07", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync(new Uri($"/api/labor/sites/{site}/lines", UriKind.Relative))).StatusCode);
        // Not set up as a person, so they can't clock on yet, but they may try.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await op.PostAsJsonAsync("/api/labor/me/sessions", new Pulaski.Contracts.Labor.ClockOnRequest { EquipmentId = line })).StatusCode);

        var supervisor = await factory.CreateClientAsAsync(Roles.Supervisor, site);
        Assert.Equal(HttpStatusCode.OK, (await supervisor.PostAsJsonAsync("/api/labor/sessions", onLine)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supervisor.GetAsync(new Uri($"/api/labor/sites/{site}/report?from=2026-10-01&to=2026-10-07", UriKind.Relative))).StatusCode);

        var planner = await factory.CreateClientAsAsync(Roles.Planner, site);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.GetAsync(new Uri($"/api/people?siteId={site}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsJsonAsync("/api/crews", new Pulaski.Contracts.Labor.CreateCrewRequest { SiteId = site, Code = "R", Name = "Red" })).StatusCode);
    }

    [Fact]
    public async Task Back_dating_a_movement_needs_its_own_permission_at_the_site()
    {
        var site = Guid.CreateVersion7();
        var lot = Guid.CreateVersion7();
        factory.Stores.Security.SetSite(SiteOf.Lot, lot, site);
        var supervisor = await factory.CreateClientAsAsync(Roles.Supervisor, site);
        var quality = await factory.CreateClientAsAsync(Roles.Quality, site);
        var request = new Contracts.Inventory.AdjustLotRequest { Version = 1, Quantity = 1m, UomId = Guid.CreateVersion7(), ReasonCodeId = Guid.CreateVersion7(), Comment = "Late" };
        var uri = new Uri($"/api/lots/{lot}/write-off", UriKind.Relative);

        // Quality may correct inventory and back-date; a supervisor may do neither.
        var backdated = request with { EffectiveAt = DateTimeOffset.UtcNow.AddDays(-1) };
        Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.PostAsJsonAsync(uri, backdated)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await quality.PostAsJsonAsync(uri, backdated)).StatusCode);
    }

    [Fact]
    public async Task A_role_that_may_correct_but_not_back_date_is_refused_only_the_back_dating()
    {
        var site = Guid.CreateVersion7();
        var lot = Guid.CreateVersion7();
        factory.Stores.Security.SetSite(SiteOf.Lot, lot, site);
        factory.Stores.Security.AddRole("corrector", "Corrector", [Permissions.InventoryCorrect]);
        var corrector = await factory.CreateClientAsAsync("corrector", site);
        var request = new Contracts.Inventory.AdjustLotRequest { Version = 1, Quantity = 1m, UomId = Guid.CreateVersion7(), ReasonCodeId = Guid.CreateVersion7(), Comment = "Late" };
        var uri = new Uri($"/api/lots/{lot}/write-off", UriKind.Relative);

        Assert.NotEqual(HttpStatusCode.Forbidden, (await corrector.PostAsJsonAsync(uri, request)).StatusCode);
        var refused = await corrector.PostAsJsonAsync(uri, request with { EffectiveAt = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("back-date", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reversing_a_production_report_needs_production_correct_at_the_jobs_site()
    {
        var site = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        factory.Stores.Security.SetSite(SiteOf.Job, job, site);
        var request = new ReverseProductionRequest { ReasonCodeId = Guid.CreateVersion7(), Comment = "Wrong job" };
        var uri = $"/api/jobs/{job}/production/{Guid.CreateVersion7()}/reverse";

        var operatorClient = await factory.CreateClientAsAsync(Roles.Operator, site);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PostAsJsonAsync(uri, request)).StatusCode);

        // A supervisor gets through to the report, which doesn't exist here.
        var supervisor = await factory.CreateClientAsAsync(Roles.Supervisor, site);
        Assert.Equal(HttpStatusCode.NotFound, (await supervisor.PostAsJsonAsync(uri, request)).StatusCode);
    }

    [Fact]
    public async Task Allocating_needs_lot_allocate_at_the_jobs_site()
    {
        var site = Guid.CreateVersion7();
        var job = Guid.CreateVersion7();
        factory.Stores.Security.SetSite(SiteOf.Job, job, site);
        var request = new Pulaski.Contracts.Inventory.AllocateLotRequest { LotId = Guid.CreateVersion7(), Quantity = 10m, UomId = Guid.CreateVersion7() };

        var operatorClient = await factory.CreateClientAsAsync(Roles.Operator, site);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PostAsJsonAsync($"/api/jobs/{job}/allocations", request)).StatusCode);

        // A planner gets through to the job, which doesn't exist here.
        var planner = await factory.CreateClientAsAsync(Roles.Planner, site);
        Assert.Equal(HttpStatusCode.NotFound, (await planner.PostAsJsonAsync($"/api/jobs/{job}/allocations", request)).StatusCode);
    }
}
