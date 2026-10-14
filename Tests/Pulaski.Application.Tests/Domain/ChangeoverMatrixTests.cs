using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

public class ChangeoverMatrixTests
{
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid OtherLine = Guid.CreateVersion7();
    private static readonly Guid White = Guid.CreateVersion7();
    private static readonly Guid Grey = Guid.CreateVersion7();
    private static readonly Guid Black = Guid.CreateVersion7();
    private static readonly Guid Light = Guid.CreateVersion7();
    private static readonly Guid Dark = Guid.CreateVersion7();

    private static readonly MaterialRef WhiteRef = new(White, Light);
    private static readonly MaterialRef GreyRef = new(Grey, Light);
    private static readonly MaterialRef BlackRef = new(Black, Dark);

    private static ChangeoverRule Rule(
        int minutes,
        Guid? fromMaterial = null,
        Guid? fromClass = null,
        Guid? toMaterial = null,
        Guid? toClass = null,
        Guid? line = null) =>
        new(Guid.CreateVersion7(), line, fromMaterial, fromClass, toMaterial, toClass, TimeSpan.FromMinutes(minutes));

    [Fact]
    public void Same_material_needs_no_changeover_even_with_a_catch_all_rule()
    {
        var matrix = new ChangeoverMatrix(Line, [Rule(60)]);

        var result = matrix.Resolve(WhiteRef, WhiteRef);

        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Equal(ChangeoverSource.None, result.Source);
    }

    [Fact]
    public void Exact_pair_beats_class_rules_and_the_catch_all()
    {
        var exact = Rule(15, fromMaterial: White, toMaterial: Grey);
        var matrix = new ChangeoverMatrix(Line, [Rule(60), Rule(30, fromClass: Light, toClass: Light), exact]);

        var result = matrix.Resolve(WhiteRef, GreyRef);

        Assert.Equal(exact.Id, result.RuleId);
        Assert.Equal(TimeSpan.FromMinutes(15), result.Duration);
    }

    [Fact]
    public void Class_rules_express_light_to_dark_cheaply_and_dark_to_light_dearly()
    {
        var matrix = new ChangeoverMatrix(Line, [Rule(20, fromClass: Light, toClass: Dark), Rule(120, fromClass: Dark, toClass: Light)]);

        Assert.Equal(TimeSpan.FromMinutes(20), matrix.Resolve(WhiteRef, BlackRef).Duration);
        Assert.Equal(TimeSpan.FromMinutes(120), matrix.Resolve(BlackRef, WhiteRef).Duration);
    }

    [Fact]
    public void Equal_total_specificity_prefers_the_incoming_side()
    {
        var toBlack = Rule(45, toMaterial: Black);
        var matrix = new ChangeoverMatrix(Line, [Rule(10, fromMaterial: White), toBlack]);

        Assert.Equal(toBlack.Id, matrix.Resolve(WhiteRef, BlackRef).RuleId);
    }

    [Fact]
    public void Line_rule_beats_an_equally_specific_site_rule_and_other_lines_are_ignored()
    {
        var lineRule = Rule(25, toClass: Dark, line: Line);
        var matrix = new ChangeoverMatrix(Line, [Rule(40, toClass: Dark), lineRule, Rule(5, toMaterial: Black, line: OtherLine)]);

        Assert.Equal(lineRule.Id, matrix.Resolve(WhiteRef, BlackRef).RuleId);
    }

    [Fact]
    public void Ties_take_the_longer_duration_regardless_of_order()
    {
        var a = Rule(30, toClass: Dark);
        var b = Rule(50, toClass: Dark);

        Assert.Equal(b.Id, new ChangeoverMatrix(Line, [a, b]).Resolve(WhiteRef, BlackRef).RuleId);
        Assert.Equal(b.Id, new ChangeoverMatrix(Line, [b, a]).Resolve(WhiteRef, BlackRef).RuleId);
    }

    [Fact]
    public void Without_a_rule_the_standard_changeover_for_the_incoming_material_applies()
    {
        var matrix = new ChangeoverMatrix(Line, [], new Dictionary<Guid, TimeSpan> { [Black] = TimeSpan.FromMinutes(35) });

        var toBlack = matrix.Resolve(WhiteRef, BlackRef);
        var toGrey = matrix.Resolve(WhiteRef, GreyRef);

        Assert.Equal((TimeSpan.FromMinutes(35), ChangeoverSource.Standard), (toBlack.Duration, toBlack.Source));
        Assert.Equal((TimeSpan.Zero, ChangeoverSource.Unknown), (toGrey.Duration, toGrey.Source));
    }

    [Fact]
    public void A_material_without_a_class_never_matches_a_class_rule()
    {
        var matrix = new ChangeoverMatrix(Line, [Rule(30, toClass: Dark)]);

        Assert.Equal(ChangeoverSource.Unknown, matrix.Resolve(WhiteRef, new MaterialRef(Guid.CreateVersion7(), null)).Source);
    }
}
