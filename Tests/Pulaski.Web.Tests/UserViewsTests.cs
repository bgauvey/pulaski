using Pulaski.Contracts.Security;
using Pulaski.Web.Services.Security;

namespace Pulaski.Web.Tests;

/// <summary>Favourites, the home page and saved filters as the screens keep them (#131).</summary>
public class UserViewsTests
{
    [Fact]
    public void A_page_is_its_place_without_query_and_a_favourite_toggles_and_keeps_the_newest()
    {
        Assert.Equal("inventory/lots", UserViews.PageOf("/inventory/lots?status=on_hold#top"));
        Assert.Equal("Lots · Inventory", UserViews.TitleOf("Lots · Inventory · Pulaski", "x"));
        Assert.Equal("inventory/lots", UserViews.TitleOf("Pulaski", "inventory/lots"));

        var lots = new FavoritePage("inventory/lots", "Lots");
        var one = UserViews.Toggle(null, lots);
        Assert.True(UserViews.IsFavorite(one, "inventory/lots"));
        Assert.Empty(UserViews.Toggle(one, lots with { Title = "Renamed" }));

        IReadOnlyList<FavoritePage> many = [.. Enumerable.Range(0, UserViews.MaxFavorites).Select(i => new FavoritePage($"p{i}", $"P{i}"))];
        var more = UserViews.Toggle(many, lots);
        Assert.Equal((UserViews.MaxFavorites, "p1", "inventory/lots"), (more.Count, more[0].Href, more[^1].Href));
    }

    [Fact]
    public void Settings_read_from_the_profile_and_a_change_is_seen_at_once()
    {
        var user = new CurrentUser();
        user.Set(new MeDto
        {
            Id = Guid.NewGuid(),
            Username = "u",
            DisplayName = "U",
            Settings = new Dictionary<string, string> { ["home"] = "\"floor\"", ["favorites"] = """[{"href":"inventory/lots","title":"Lots"}]""", ["filters:x"] = "{broken" },
        });
        Assert.Equal("floor", user.Setting<string>(UserViews.HomeKey));
        Assert.Equal("Lots", Assert.Single(user.Setting<List<FavoritePage>>(UserViews.FavoritesKey)!).Title);
        Assert.Null(user.Setting<Dictionary<string, string>>("filters:x"));
        Assert.Null(user.Setting<string>("filters:missing"));
    }
}
