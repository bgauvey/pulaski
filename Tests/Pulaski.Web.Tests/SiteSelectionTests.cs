using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Security;
using Pulaski.Web.Services;
using Pulaski.Web.Services.Security;

namespace Pulaski.Web.Tests;

/// <summary>Site pickers offer only the sites where the user may see the current screen (D-039, #43).</summary>
public class SiteSelectionTests
{
    private static readonly EquipmentDto PlantA = Site("PLT1");
    private static readonly EquipmentDto PlantB = Site("PLT2");
    private static readonly EquipmentDto Closed = Site("OLD") with { IsActive = false };

    private static EquipmentDto Site(string code)
    {
        var id = Guid.CreateVersion7();
        return new EquipmentDto { Id = id, Code = code, Name = code, Level = "site", EquipmentRole = "site", SiteId = id, IsActive = true };
    }

    // Labor only at PLT1; inventory at both; the Overview everywhere.
    private static SiteSelection Selection()
    {
        var user = new CurrentUser();
        user.Set(new MeDto
        {
            Id = Guid.CreateVersion7(),
            Username = "sam",
            DisplayName = "Sam",
            Permissions =
            [
                new PermissionGrantDto(PermissionCodes.LaborView, [PlantA.Id]),
                new PermissionGrantDto(PermissionCodes.InventoryView, [PlantA.Id, PlantB.Id]),
                new PermissionGrantDto(PermissionCodes.OverviewView, null),
            ],
        });
        var selection = new SiteSelection(user);
        selection.Use([PlantA, PlantB, Closed]);
        return selection;
    }

    [Fact]
    public void A_screen_offers_the_sites_where_the_user_holds_one_of_its_views()
    {
        var sites = Selection();

        sites.ScreenViews = [PermissionCodes.LaborView];
        Assert.Equal(["PLT1"], sites.Sites.Select(s => s.Code));

        sites.ScreenViews = [PermissionCodes.InventoryView];
        Assert.Equal(["PLT1", "PLT2"], sites.Sites.Select(s => s.Code));

        sites.ScreenViews = [PermissionCodes.LaborView, PermissionCodes.OverviewView];
        Assert.Equal(["PLT1", "PLT2"], sites.Sites.Select(s => s.Code));

        // A screen with no view to check, and the list for choosing where a role applies, show every active site.
        sites.ScreenViews = null;
        Assert.Equal(["PLT1", "PLT2"], sites.Sites.Select(s => s.Code));
        sites.ScreenViews = [PermissionCodes.LaborView];
        Assert.Equal(["PLT1", "PLT2"], sites.AllSites.Select(s => s.Code));
    }

    [Fact]
    public void The_chosen_site_is_kept_for_the_screens_that_can_show_it()
    {
        var sites = Selection();
        sites.ScreenViews = [PermissionCodes.InventoryView];
        Assert.Equal(PlantA.Id, sites.SiteId);
        sites.SiteId = PlantB.Id;

        // Labor can't show PLT2, so it shows PLT1; back on Inventory, PLT2 is still chosen.
        sites.ScreenViews = [PermissionCodes.LaborView];
        Assert.Equal((PlantA.Id, "PLT1"), (sites.SiteId, sites.Site?.Code));
        sites.ScreenViews = [PermissionCodes.InventoryView];
        Assert.Equal(PlantB.Id, sites.SiteId);

        Assert.Equal(["PLT1", "PLT2"], sites.ForScreen([PlantA, PlantB]).Select(s => s.Code));
        sites.ScreenViews = [PermissionCodes.LaborView];
        Assert.Equal(["PLT1"], sites.ForScreen([PlantA, PlantB]).Select(s => s.Code));
    }

    [Fact]
    public void A_screen_the_user_holds_at_no_site_has_no_site()
    {
        var sites = Selection();
        sites.ScreenViews = [PermissionCodes.ShiftsView];

        Assert.Empty(sites.Sites);
        Assert.Null(sites.SiteId);
    }
}
