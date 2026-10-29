using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Where a reason may sit in its tree (D-062).</summary>
public class ReasonTreeTests
{
    private static readonly Guid SiteA = Guid.CreateVersion7();
    private static readonly Guid SiteB = Guid.CreateVersion7();

    private static ReasonCode R(string code, Guid? site = null, Guid? parent = null, bool group = false, bool active = true, string kind = "hold") =>
        new(Guid.CreateVersion7(), site, kind, code, code, false, 0, active, null, parent, group);

    [Fact]
    public void A_reason_goes_under_a_group_of_its_kind_whose_scope_covers_it()
    {
        var everywhere = R("QUALITY", group: true);
        var siteGroup = R("LAB", SiteA, group: true);
        var leaf = R("SPEC", SiteA);
        var other = R("OTHER", SiteB);
        var notGroup = R("PLAIN");
        var wrongKind = R("SCRAPS", group: true, kind: "scrap");
        ReasonCode[] tree = [everywhere, siteGroup, leaf, other, notGroup];

        Assert.Null(ReasonTree.WhyNotUnder(tree, leaf, everywhere.Id));
        Assert.Null(ReasonTree.WhyNotUnder(tree, leaf, siteGroup.Id));
        Assert.Null(ReasonTree.WhyNotUnder(tree, leaf, null));
        Assert.Contains("another site", ReasonTree.WhyNotUnder(tree, other, siteGroup.Id), StringComparison.Ordinal);
        Assert.Contains("for every site can only go under", ReasonTree.WhyNotUnder(tree, everywhere with { Id = Guid.CreateVersion7() }, siteGroup.Id), StringComparison.Ordinal);
        Assert.Contains("isn't a group", ReasonTree.WhyNotUnder(tree, leaf, notGroup.Id), StringComparison.Ordinal);
        Assert.Contains("isn't one of this kind's", ReasonTree.WhyNotUnder([.. tree, wrongKind], leaf, wrongKind.Id), StringComparison.Ordinal);
        Assert.Contains("isn't one of this kind's", ReasonTree.WhyNotUnder(tree, leaf, Guid.CreateVersion7()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_never_goes_inside_itself_and_a_tree_stays_within_five_levels()
    {
        var top = R("A", group: true);
        var middle = R("B", parent: top.Id, group: true);
        var bottom = R("C", parent: middle.Id, group: true);
        ReasonCode[] tree = [top, middle, bottom];

        Assert.Contains("is inside it", ReasonTree.WhyNotUnder(tree, top, bottom.Id), StringComparison.Ordinal);
        Assert.Contains("is inside it", ReasonTree.WhyNotUnder(tree, top, top.Id), StringComparison.Ordinal);
        Assert.Equal([middle.Id, top.Id], ReasonTree.Ancestors(tree, bottom).Select(r => r.Id));

        // A, B, C, D, E is five levels; one more is too many, whether added at the bottom or by moving a branch under it.
        var d = R("D", parent: bottom.Id, group: true);
        var e = R("E", parent: d.Id, group: true);
        ReasonCode[] deep = [.. tree, d, e];
        Assert.Contains("at most 5", ReasonTree.WhyNotUnder(deep, R("F"), e.Id), StringComparison.Ordinal);
        var branch = R("X", group: true);
        ReasonCode[] withBranch = [.. tree, d, branch, R("Y", parent: branch.Id)];
        Assert.Null(ReasonTree.WhyNotUnder(withBranch, branch, bottom.Id));
        Assert.Contains("6 levels", ReasonTree.WhyNotUnder(withBranch, branch, d.Id), StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_is_switched_off_only_when_what_is_in_it_is_and_nothing_is_switched_on_under_one_that_is_off()
    {
        var group = R("G", group: true);
        var child = R("C1", parent: group.Id);
        var off = R("C2", parent: group.Id, active: false);
        ReasonCode[] tree = [group, child, off];

        Assert.Contains("C1", ReasonTree.WhyNotActive(tree, group, false), StringComparison.Ordinal);
        Assert.Null(ReasonTree.WhyNotActive(tree, child, false));
        Assert.Null(ReasonTree.WhyNotActive([group, child with { IsActive = false }, off], group, false));

        var groupOff = group with { IsActive = false };
        Assert.Contains("switched off", ReasonTree.WhyNotActive([groupOff, off], off, true), StringComparison.Ordinal);
        Assert.Contains("switched off", ReasonTree.WhyNotUnder([groupOff, child], child, groupOff.Id), StringComparison.Ordinal);
        Assert.Null(ReasonTree.WhyNotUnder([groupOff, off], off, groupOff.Id));
    }

    [Fact]
    public void A_reason_never_used_becomes_a_group_and_an_empty_group_a_reason()
    {
        var group = R("MECH", group: true);
        var jam = R("JAM", parent: group.Id);
        var spare = R("SPARE");
        var tree = new[] { group, jam, spare };

        Assert.Null(ReasonTree.WhyNotConvert(tree, spare, toGroup: true, []));
        Assert.Contains("ts.downtime", ReasonTree.WhyNotConvert(tree, jam, toGroup: true, ["ts.downtime"]), StringComparison.Ordinal);
        Assert.Contains("JAM", ReasonTree.WhyNotConvert(tree, group, toGroup: false, []), StringComparison.Ordinal);
        Assert.Null(ReasonTree.WhyNotConvert(tree, R("EMPTY", group: true), toGroup: false, []));
        Assert.Null(ReasonTree.WhyNotConvert(tree, spare, toGroup: false, ["ts.downtime"]));   // already a reason: nothing to do
    }
}
