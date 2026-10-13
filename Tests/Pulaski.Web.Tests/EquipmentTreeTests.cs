using Pulaski.Contracts.EquipmentModel;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

public class EquipmentTreeTests
{
    // ENT
    // └─ PLT1
    //    ├─ EXT-01
    //    │  └─ EXT-01-SAW
    //    └─ EXT-02
    private static readonly EquipmentDto Ent = Node("ENT", "Enterprise", null);
    private static readonly EquipmentDto Plant = Node("PLT1", "Plant 1", Ent);
    private static readonly EquipmentDto Line1 = Node("EXT-01", "Extrusion line 1", Plant);
    private static readonly EquipmentDto Saw = Node("EXT-01-SAW", "Saw", Line1);
    private static readonly EquipmentDto Line2 = Node("EXT-02", "Extrusion line 2", Plant);

    private static readonly EquipmentTree Tree = new([Ent, Plant, Line1, Saw, Line2]);

    [Fact]
    public void Collapsed_nodes_hide_their_descendants()
    {
        var rows = Tree.VisibleRows(new HashSet<Guid> { Ent.Id, Plant.Id });

        Assert.Equal(["ENT", "PLT1", "EXT-01", "EXT-02"], rows.Select(r => r.Item.Code));
        Assert.Equal([0, 1, 2, 2], rows.Select(r => r.Depth));
        Assert.True(rows[2].HasChildren);
        Assert.False(rows[2].IsExpanded);
        Assert.False(rows[3].HasChildren);
    }

    [Fact]
    public void Keeps_the_api_sibling_order()
    {
        var reversed = new EquipmentTree([Ent, Plant, Line2, Line1]);

        var rows = reversed.VisibleRows(new HashSet<Guid> { Ent.Id, Plant.Id });

        Assert.Equal(["ENT", "PLT1", "EXT-02", "EXT-01"], rows.Select(r => r.Item.Code));
    }

    [Fact]
    public void Filter_shows_matches_with_their_ancestors_expanded()
    {
        var rows = Tree.VisibleRows(new HashSet<Guid>(), "saw");

        Assert.Equal(["ENT", "PLT1", "EXT-01", "EXT-01-SAW"], rows.Select(r => r.Item.Code));
        Assert.Equal([false, false, false, true], rows.Select(r => r.IsMatch));
        Assert.All(rows.Take(3), r => Assert.True(r.IsExpanded));
    }

    [Fact]
    public void Filter_matches_name_ignoring_case()
    {
        var rows = Tree.VisibleRows(new HashSet<Guid>(), "LINE 2");

        Assert.Equal("EXT-02", Assert.Single(rows, r => r.IsMatch).Item.Code);
    }

    [Fact]
    public void Row_whose_parent_is_missing_becomes_a_root()
    {
        var tree = new EquipmentTree([Line1, Saw]);

        Assert.Equal([Line1.Id], tree.Roots.Select(r => r.Id));
        Assert.Equal([Saw.Id], tree.ChildrenOf(Line1.Id).Select(c => c.Id));
    }

    [Fact]
    public void Ancestors_run_from_parent_to_root()
    {
        Assert.Equal(["EXT-01", "PLT1", "ENT"], Tree.AncestorsOf(Saw.Id).Select(a => a.Code));
    }

    [Theory]
    [InlineData("EXT-01", "ArrowDown", "EXT-02")]
    [InlineData("EXT-01", "ArrowUp", "PLT1")]
    [InlineData("EXT-01", "Home", "ENT")]
    [InlineData("PLT1", "End", "EXT-02")]
    [InlineData("EXT-02", "ArrowLeft", "PLT1")]
    [InlineData("PLT1", "ArrowRight", "EXT-01")]
    public void Arrow_keys_move_the_selection(string from, string key, string to)
    {
        var rows = Tree.VisibleRows(new HashSet<Guid> { Ent.Id, Plant.Id });

        var move = EquipmentTree.Navigate(rows, IdOf(from), key);

        Assert.Equal(IdOf(to), move.Select);
    }

    [Fact]
    public void Right_expands_a_collapsed_branch_and_left_collapses_an_open_one()
    {
        var rows = Tree.VisibleRows(new HashSet<Guid> { Ent.Id, Plant.Id });

        Assert.Equal(new TreeMove(Expand: Line1.Id), EquipmentTree.Navigate(rows, Line1.Id, "ArrowRight"));
        Assert.Equal(new TreeMove(Collapse: Plant.Id), EquipmentTree.Navigate(rows, Plant.Id, "ArrowLeft"));
    }

    [Fact]
    public void Keys_at_the_edges_do_nothing()
    {
        var rows = Tree.VisibleRows(new HashSet<Guid> { Ent.Id, Plant.Id });

        Assert.Equal(default, EquipmentTree.Navigate(rows, Ent.Id, "ArrowUp"));
        Assert.Equal(default, EquipmentTree.Navigate(rows, Line2.Id, "ArrowDown"));
        Assert.Equal(default, EquipmentTree.Navigate(rows, Line2.Id, "ArrowRight"));
        Assert.Equal(default, EquipmentTree.Navigate(rows, Line2.Id, "Enter"));
    }

    [Fact]
    public void Down_with_nothing_selected_selects_the_first_row()
    {
        var rows = Tree.VisibleRows(new HashSet<Guid>());

        Assert.Equal(Ent.Id, EquipmentTree.Navigate(rows, null, "ArrowDown").Select);
    }

    private static Guid IdOf(string code) => new[] { Ent, Plant, Line1, Saw, Line2 }.Single(e => e.Code == code).Id;

    private static EquipmentDto Node(string code, string name, EquipmentDto? parent) => new()
    {
        Id = Guid.CreateVersion7(),
        ParentId = parent?.Id,
        Code = code,
        Name = name,
        Level = "area",
        EquipmentRole = "area",
        IsActive = true,
    };
}
