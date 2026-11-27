using Pulaski.Domain.EquipmentModel;

namespace Pulaski.Application.Tests.Domain;

/// <summary>A copied line's codes follow the new line's, and must be free (#140).</summary>
public class LineCopyRulesTests
{
    [Fact]
    public void Children_take_the_new_lines_code_in_place_of_the_old_or_in_front()
    {
        Assert.Equal("EXT-03-SAW", LineCopyRules.ChildCode("EXT-01", "EXT-03", "EXT-01-SAW"));
        Assert.Equal("EXT-03-SAW", LineCopyRules.ChildCode("EXT-01", "EXT-03", "ext-01-SAW"));
        Assert.Equal("EXT-03-HOPPER", LineCopyRules.ChildCode("EXT-01", "EXT-03", "HOPPER"));
    }

    [Fact]
    public void Codes_must_be_given_short_free_at_the_site_and_not_repeated()
    {
        static LineCopyItem I(string code, string name = "N") => new(Guid.NewGuid(), null, 0, code, name);
        var taken = new HashSet<string>(["EXT-01"], StringComparer.OrdinalIgnoreCase);
        Assert.Empty(LineCopyRules.Problems([I("EXT-03"), I("EXT-03-SAW")], taken));
        Assert.Single(LineCopyRules.Problems([I("ext-01")], taken));
        Assert.Single(LineCopyRules.Problems([I("A"), I("a")], taken));
        Assert.Single(LineCopyRules.Problems([I(" ")], taken));
        Assert.Single(LineCopyRules.Problems([I(new string('x', 51))], taken));
        Assert.Single(LineCopyRules.Problems([I("B", " ")], taken));
    }
}
