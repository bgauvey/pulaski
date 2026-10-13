using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;

namespace Pulaski.Application.Tests.Domain;

public class EquipmentTests
{
    private static readonly Equipment Enterprise =
        Equipment.Create(Guid.CreateVersion7(), null, "ENT", "Enterprise", null, "enterprise", "enterprise", false, false, false, null);

    [Fact]
    public void Site_owns_itself_and_children_inherit_it()
    {
        var site = Equipment.Create(Guid.CreateVersion7(), Enterprise, "S", "Site", null, "site", "site", false, false, false, "UTC");
        var area = Equipment.Create(Guid.CreateVersion7(), site, "A", "Area", null, "area", "area", false, false, false, null);

        Assert.Null(Enterprise.SiteId);
        Assert.Equal(site.Id, site.SiteId);
        Assert.Equal(site.Id, area.SiteId);
        Assert.Equal(site.Id, area.ParentId);
    }

    [Theory]
    [InlineData("enterprise", "enterprise")]
    [InlineData("work_unit", "production_line")]
    public void Rejects_bad_placement_or_role(string level, string role) =>
        Assert.Throws<DomainRuleException>(
            () => Equipment.Create(Guid.CreateVersion7(), Enterprise, "X", "X", null, level, role, false, false, false, null));

    [Fact]
    public void Non_enterprise_needs_a_parent() =>
        Assert.Throws<DomainRuleException>(
            () => Equipment.Create(Guid.CreateVersion7(), null, "S", "Site", null, "site", "site", false, false, false, "UTC"));
}
