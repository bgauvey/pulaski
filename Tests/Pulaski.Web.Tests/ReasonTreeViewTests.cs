using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

/// <summary>How the Reasons screen and pickers show reason trees (D-062).</summary>
public class ReasonTreeViewTests
{
    private static readonly Guid Site = Guid.CreateVersion7();

    private static ReasonCodeAdminDto R(string code, Guid? parent = null, bool group = false, Guid? site = null, string kind = "hold", string? path = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = kind,
        Code = code,
        Name = code,
        SiteId = site,
        ParentId = parent,
        IsSelectable = !group,
        GroupPath = path,
    };

    [Fact]
    public void A_reason_is_offered_only_groups_of_its_kind_outside_it_whose_scope_covers_it()
    {
        var top = R("TOP", group: true);
        var inner = R("INNER", top.Id, group: true, path: "TOP");
        var siteOnly = R("SITE", group: true, site: Site);
        var otherKind = R("SCRAP", group: true, kind: "scrap");
        var leaf = R("LEAF", inner.Id);
        ReasonCodeAdminDto[] all = [top, inner, siteOnly, otherKind, leaf];

        Assert.Equal([top.Id, inner.Id, siteOnly.Id], ReasonTreeView.Groups(all, "hold", null, Site).Select(g => g.Id));
        Assert.Equal([siteOnly.Id], ReasonTreeView.Groups(all, "hold", top, Site).Select(g => g.Id));
        Assert.DoesNotContain(ReasonTreeView.Groups(all, "hold", null, null), g => g.Id == siteOnly.Id);
        Assert.Equal("TOP › INNER", ReasonTreeView.GroupLabel(inner));
    }

    [Fact]
    public void Pickers_name_a_reason_under_its_groups()
    {
        Assert.Equal("Mechanical › Jam", Display.ReasonLabel(new ReasonCodeDto { Id = Guid.CreateVersion7(), Kind = "utilization", Code = "JAM", Name = "Jam", GroupPath = "Mechanical" }));
        Assert.Equal("Jam", Display.ReasonLabel(new ReasonCodeDto { Id = Guid.CreateVersion7(), Kind = "utilization", Code = "JAM", Name = "Jam" }));
    }
}
