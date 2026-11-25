using Pulaski.Contracts.Security;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

/// <summary>The sidebar groups like screens under their main one, and shows each user only what they may open (#199).</summary>
public class NavTreeTests
{
    private static IEnumerable<NavLinkItem> All(IReadOnlyList<NavSection> sections) =>
        sections.SelectMany(s => s.Entries).SelectMany(e => e.Link is { } l ? [l, .. e.Children] : e.Children);

    [Fact]
    public void Every_screen_is_in_the_menu_once()
    {
        var hrefs = All(NavTree.Sections).Select(l => l.Href).ToList();
        Assert.Equal(hrefs.Count, hrefs.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(30, hrefs.Count);
        Assert.Contains("machine-profiles", NavTree.Sections[1].Entries.Single(e => e.Key == "equipment").Children.Select(c => c.Href));
    }

    [Fact]
    public void A_user_sees_only_what_they_may_open_and_a_group_without_its_main_page_becomes_a_folder()
    {
        // Someone who keeps users but not people: People's group is a folder holding Users.
        var visible = NavTree.Visible(l => l.Permission is PermissionCodes.Security or PermissionCodes.OverviewView);
        Assert.Equal(["", "users"], All(visible).Select(l => l.Href));
        var people = visible.SelectMany(s => s.Entries).Single(e => e.Key == "people");
        Assert.Null(people.Link);
        Assert.Equal("People", people.FolderText);

        // Nothing at all to see: no sections.
        Assert.Empty(NavTree.Visible(_ => false));
    }

    [Theory]
    [InlineData("edge-nodes/3f2a", "equipment")]
    [InlineData("schedule?line=EXT", "planning")]
    [InlineData("handover", "floor")]
    [InlineData("quality/specs/12", "materials")]
    [InlineData("machines", null)]
    [InlineData("", null)]
    public void The_open_screens_group_is_found_by_its_address(string path, string? group) =>
        Assert.Equal(group, NavTree.GroupAt(NavTree.Sections, path));

    [Fact]
    public void A_screen_matches_its_own_address_and_those_below_it_only()
    {
        Assert.True(NavTree.IsAt("equipment", "equipment/12"));
        Assert.False(NavTree.IsAt("equipment", "equipment-types"));
        Assert.True(NavTree.IsAt("", ""));
        Assert.False(NavTree.IsAt("", "floor"));
    }
}
