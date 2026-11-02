using Pulaski.Domain;
using Pulaski.Domain.Quality;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Specifications and results (#65, D-085): limits, conformance and the draft-to-release lifecycle.</summary>
public class QualityTests
{
    private static readonly Characteristic Wall = new(Guid.CreateVersion7(), "WALL", "Wall thickness", null, CharacteristicKinds.Variable, null, 2, true);
    private static readonly Characteristic Surface = new(Guid.CreateVersion7(), "SURF", "Surface defects", null, CharacteristicKinds.Attribute, null, 0, true);

    private static SpecItem Item(Characteristic c, decimal? lsl = null, decimal? usl = null, int? maxDefects = null, decimal? target = null) =>
        new(Guid.CreateVersion7(), c.Id, lsl, target, usl, maxDefects, HoldOnFail: false, 0);

    [Fact]
    public void Every_value_must_be_within_the_limits_and_defects_no_more_than_allowed()
    {
        var wall = Item(Wall, 3.2m, 3.4m);
        Assert.True(QualityCheck.Conforms(QualityMeasurement.Measured(3.2m, 3.3m, 3.4m), wall));
        Assert.False(QualityCheck.Conforms(QualityMeasurement.Measured(3.3m, 3.45m), wall));
        Assert.Equal("1 of 2 values above 3.4", QualityCheck.Failure(QualityMeasurement.Measured(3.3m, 3.45m), wall));
        Assert.Equal("the value is below 3.2", QualityCheck.Failure(QualityMeasurement.Measured(3.1m), wall));
        Assert.True(QualityCheck.Conforms(QualityMeasurement.Measured(99m), Item(Wall, lsl: 3.2m)));

        var surface = Item(Surface, maxDefects: 1);
        Assert.True(QualityCheck.Conforms(QualityMeasurement.Counted(50, 1), surface));
        Assert.False(QualityCheck.Conforms(QualityMeasurement.Counted(50, 2), surface));
        Assert.Equal("2 defects, at most 1 allowed", QualityCheck.Failure(QualityMeasurement.Counted(50, 2), surface));

        // Without limits in force nothing can be said.
        Assert.Null(QualityCheck.Conforms(QualityMeasurement.Measured(3.3m), null));
    }

    [Fact]
    public void Limits_read_as_people_say_them_and_samples_carry_their_statistics()
    {
        Assert.Equal("3.2 – 3.4 (target 3.3)", QualityCheck.Limits(Item(Wall, 3.2m, 3.4m, target: 3.3m)));
        Assert.Equal("at least 3.2", QualityCheck.Limits(Item(Wall, lsl: 3.2m)));
        Assert.Equal("at most 0", QualityCheck.Limits(Item(Wall, usl: 0m)));
        Assert.Equal("at most 1 defect", QualityCheck.Limits(Item(Surface, maxDefects: 1)));

        var stats = SampleStatistics.Of(QualityMeasurement.Measured(3.2m, 3.4m));
        Assert.Equal((3.3m, 0.2m), (stats.Mean!.Value, stats.Range!.Value));
        Assert.Equal(0.1414213562m, stats.StdDev);
        Assert.Null(SampleStatistics.Of(QualityMeasurement.Measured(3.3m)).StdDev);
    }

    [Fact]
    public void A_measurement_must_fit_its_characteristic()
    {
        Assert.Null(QualityMeasurement.Measured(3.3m).Problem(CharacteristicKinds.Variable));
        Assert.NotNull(new QualityMeasurement([], null, null).Problem(CharacteristicKinds.Variable));
        Assert.NotNull(QualityMeasurement.Counted(5, 0).Problem(CharacteristicKinds.Variable));
        Assert.NotNull(QualityMeasurement.Measured(1m).Problem(CharacteristicKinds.Attribute));
        Assert.NotNull(QualityMeasurement.Counted(5, 6).Problem(CharacteristicKinds.Attribute));
        Assert.Null(QualityMeasurement.Counted(5, 0).Problem(CharacteristicKinds.Attribute));
    }

    [Fact]
    public void A_specification_names_each_characteristic_once_with_limits_that_fit_its_kind()
    {
        Assert.Contains(new SpecDefinition([Item(Wall, 3.4m, 3.2m)]).StructuralProblems(), p => p.Code == "limits_order");
        Assert.Contains(new SpecDefinition([Item(Wall, 3.2m, 3.4m, target: 3.5m)]).StructuralProblems(), p => p.Code == "target_outside");
        Assert.Contains(new SpecDefinition([Item(Wall, 3.2m), Item(Wall, usl: 3.4m)]).StructuralProblems(), p => p.Code == "duplicate_characteristic");
        Assert.Contains(SpecDefinition.Empty.ReleaseProblems(), p => p.Code == "no_items");

        Assert.Equal("no_limits", Assert.Single(SpecDefinition.KindProblems(Item(Wall), Wall)).Code);
        Assert.Equal("kind_mismatch", Assert.Single(SpecDefinition.KindProblems(Item(Wall, maxDefects: 1), Wall)).Code);
        Assert.Equal("no_max_defects", Assert.Single(SpecDefinition.KindProblems(Item(Surface), Surface)).Code);
        Assert.Equal("kind_mismatch", Assert.Single(SpecDefinition.KindProblems(Item(Surface, usl: 2m), Surface)).Code);
        Assert.Empty(SpecDefinition.KindProblems(Item(Surface, maxDefects: 0), Surface));
    }

    [Fact]
    public void A_version_is_drafted_released_once_made_obsolete_and_copied_with_new_item_ids()
    {
        var draft = SpecVersion.FirstDraft(Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.Throws<DomainRuleException>(() => draft.Release(DateTimeOffset.UnixEpoch, "qa", null));

        var saved = draft.SaveDraft(new SpecDefinition([Item(Wall, 3.2m, 3.4m)]));
        var released = saved.Release(DateTimeOffset.UnixEpoch, "qa", "First issue");
        Assert.Equal((SpecVersionStatus.Released, "qa"), (released.Status, released.ReleasedBy));
        Assert.Throws<DomainRuleException>(() => released.SaveDraft(SpecDefinition.Empty));

        var next = released.NextDraft(Guid.CreateVersion7(), Guid.CreateVersion7);
        Assert.Equal((2, SpecVersionStatus.Draft), (next.Number, next.Status));
        Assert.NotEqual(released.Definition.Items[0].Id, next.Definition.Items[0].Id);
        Assert.Equal(SpecVersionStatus.Obsolete, released.MakeObsolete().Status);
        Assert.Throws<DomainRuleException>(() => next.MakeObsolete());
        Assert.Equal("Upper-case letters, digits, '-' and '_' only.", Characteristic.CodeProblem("wall"));
    }
}
