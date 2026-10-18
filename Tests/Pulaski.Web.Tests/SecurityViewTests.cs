using Pulaski.Contracts.Security;
using Pulaski.Web.Services.Security;

namespace Pulaski.Web.Tests;

/// <summary>What the UI shows a user may do, and where signing in may send them (D-037).</summary>
public class SecurityViewTests
{
    private static readonly Guid PlantA = Guid.CreateVersion7();
    private static readonly Guid PlantB = Guid.CreateVersion7();

    private static CurrentUser User(params PermissionGrantDto[] permissions)
    {
        var user = new CurrentUser();
        user.Set(new MeDto
        {
            Id = Guid.CreateVersion7(),
            Username = "sam",
            DisplayName = "Sam",
            Roles = [new RoleGrantDto { AssignmentId = Guid.CreateVersion7(), RoleCode = "supervisor", RoleName = "Supervisor", SiteId = PlantA, SiteCode = "PLT1" }],
            Permissions = permissions,
        });
        return user;
    }

    [Fact]
    public void A_permission_counts_at_its_sites_or_everywhere()
    {
        var user = User(new PermissionGrantDto(PermissionCodes.JobExecute, [PlantA]), new PermissionGrantDto(PermissionCodes.Units, null));

        Assert.True(user.Can(PermissionCodes.JobExecute, PlantA));
        Assert.False(user.Can(PermissionCodes.JobExecute, PlantB));
        Assert.True(user.Can(PermissionCodes.JobExecute));
        Assert.True(user.Can(PermissionCodes.Units, PlantB));
        Assert.False(user.Can(PermissionCodes.InventoryCorrect, PlantA));
        Assert.Equal("Supervisor at PLT1", user.RoleSummary);
        Assert.False(new CurrentUser().Can(PermissionCodes.Units));
    }

    [Theory]
    [InlineData("/inventory?site=1", "/inventory?site=1")]
    [InlineData(null, "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("/account/logout", "/")]
    public void Signing_in_returns_only_to_a_page_of_this_site(string? returnUrl, string expected) =>
        Assert.Equal(expected, AccountSession.SafeReturnUrl(returnUrl));

    [Fact]
    public void A_user_starts_at_the_first_screen_they_may_see_or_has_none()
    {
        var floor = User(new PermissionGrantDto(PermissionCodes.FloorView, [PlantA]), new PermissionGrantDto(PermissionCodes.JobExecute, [PlantA]));
        Assert.True(floor.CanSeeAnything);
        Assert.Equal("floor", floor.FirstScreen);
        Assert.False(floor.CanAny([PermissionCodes.ScheduleView, PermissionCodes.OverviewView]));

        var nothing = User(new PermissionGrantDto(PermissionCodes.SiteSettings, [PlantA]));
        Assert.False(nothing.CanSeeAnything);
        Assert.Null(nothing.FirstScreen);

        Assert.Equal("users", User(new PermissionGrantDto(PermissionCodes.Security, null)).FirstScreen);
    }
}
