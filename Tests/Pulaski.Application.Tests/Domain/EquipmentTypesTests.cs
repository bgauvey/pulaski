using Pulaski.Domain.EquipmentModel;

namespace Pulaski.Application.Tests.Domain;

public class EquipmentTypesTests
{
    [Theory]
    [InlineData("work_center", "production_line", true)]
    [InlineData("equipment_module", "feed_point", true)]
    [InlineData("site", "area", false)]
    [InlineData("work_unit", "production_line", false)]
    public void Validates_level_role_pairs(string level, string role, bool expected) =>
        Assert.Equal(expected, EquipmentTypes.IsValidLevelRole(level, role));

    [Fact]
    public void Level_roles_are_listed_in_hierarchy_order()
    {
        var ranks = EquipmentTypes.LevelRoles.Select(t => EquipmentTypes.Levels.ToList().IndexOf(t.Level)).ToList();
        Assert.DoesNotContain(-1, ranks);
        Assert.Equal(ranks.Order(), ranks);
    }
}
