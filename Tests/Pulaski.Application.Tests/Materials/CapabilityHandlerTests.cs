using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Domain;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Materials;

/// <summary>Adding, changing and ending capabilities, and the run standard that follows them (D-034).</summary>
public class CapabilityHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _m;
    private readonly Guid _kg;
    private readonly Guid _ea;
    private readonly CapabilityLine _line;

    public CapabilityHandlerTests()
    {
        _m = _f.Stores.Units.AddUnit("m", "length", 1m).Id;
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m).Id;
        _ea = _f.Stores.Units.AddUnit("ea", "count", 1m).Id;
        foreach (var (code, id) in new[] { ("m", _m), ("kg", _kg), ("ea", _ea) })
        {
            _f.Stores.MaterialAdmin.AddUom(code, id: id);
        }

        _line = Capabilities.AddLine("EXT-01");
    }

    private InMemoryCapabilities Capabilities => _f.Stores.Capabilities;

    private AddCapabilityHandler Add() => new(
        _f.Stores.UnitOfWork, Capabilities, _f.Stores.MaterialAdmin, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private UpdateCapabilityHandler Update() => new(
        _f.Stores.UnitOfWork, Capabilities, _f.Stores.MaterialAdmin, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private EndCapabilityHandler End() => new(
        _f.Stores.UnitOfWork, Capabilities, _f.Stores.MaterialAdmin, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private async Task<Guid> MaterialAsync(bool isActive = true)
    {
        var material = Material.CreateLocal(Guid.CreateVersion7(), new MaterialChange("PRF-" + Guid.NewGuid().ToString("N")[..4], "Profile", null, "finished", null, _m, true, null, isActive));
        await _f.Stores.MaterialAdmin.InsertAsync(material with { IsActive = isActive }, HandlerFixture.Now, CancellationToken.None);
        return material.Id;
    }

    private AddCapabilityCommand AddCommand(Guid material, decimal rate = 900m, Guid? unit = null, Guid? line = null, DateTimeOffset? from = null) =>
        new(material, line ?? _line.Id, rate, unit ?? _m, 45, 2m, false, from, null);

    [Fact]
    public async Task Adding_a_capability_creates_the_run_standard_from_the_ideal_rate()
    {
        var material = await MaterialAsync();

        var added = await Add().HandleAsync(AddCommand(material), CancellationToken.None);

        var standard = Capabilities.StandardFor(_line.Id, material)!;
        Assert.Equal((900m, _m, CalculationBasis.IdealRate), (standard.CalculatedRatePerHour, standard.UomId, standard.CalculationBasis));
        Assert.Equal(HandlerFixture.Now, Capabilities.Find(added.Id)!.Value.EffectiveFrom);
        Assert.Contains("Planning rate set to 900 m/h", Assert.Single(added.Notes), StringComparison.Ordinal);
        Assert.Equal(
            [EventTypes.CapabilityAdded, EventTypes.RunStandardCreated],
            _f.Stores.Events.Appended.Select(e => e.Type));
    }

    [Fact]
    public async Task A_capability_needs_an_active_line_that_runs_jobs_an_active_material_a_unit_that_converts_and_its_own_period()
    {
        var material = await MaterialAsync();
        var department = Capabilities.AddLine("EXTRUSION", canRunJob: false);
        var retired = Capabilities.AddLine("EXT-09", isActive: false);

        await Assert.ThrowsAsync<DomainRuleException>(() => Add().HandleAsync(AddCommand(material, line: department.Id), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => Add().HandleAsync(AddCommand(material, line: retired.Id), CancellationToken.None));
        var inactive = await MaterialAsync(isActive: false);
        await Assert.ThrowsAsync<DomainRuleException>(() => Add().HandleAsync(AddCommand(inactive), CancellationToken.None));
        var unit = await Assert.ThrowsAsync<DomainRuleException>(() => Add().HandleAsync(AddCommand(material, unit: _ea), CancellationToken.None));
        Assert.Contains("doesn't convert", unit.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ValidationException>(() => Add().HandleAsync(AddCommand(material, rate: 0m), CancellationToken.None));

        await Add().HandleAsync(AddCommand(material), CancellationToken.None);
        var overlap = await Assert.ThrowsAsync<DomainRuleException>(() => Add().HandleAsync(AddCommand(material, from: HandlerFixture.Now.AddDays(30)), CancellationToken.None));
        Assert.Contains("can't overlap", overlap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_ideal_rate_recalculates_the_planning_rate_but_never_a_modified_one()
    {
        var material = await MaterialAsync();
        var added = await Add().HandleAsync(AddCommand(material), CancellationToken.None);
        var standard = Capabilities.StandardFor(_line.Id, material)!;
        Capabilities.PutRunStandard(standard.Modify(800m, "planner", "Die 4", HandlerFixture.Now));
        var capability = Capabilities.Find(added.Id)!;

        var changed = await Update().HandleAsync(
            new UpdateCapabilityCommand(added.Id, capability.Version, 1000m, _m, 45, 2m, true, capability.Value.EffectiveFrom, null), CancellationToken.None);

        var after = Capabilities.StandardFor(_line.Id, material)!;
        Assert.Equal((1000m, 800m), (after.CalculatedRatePerHour, after.EffectiveRatePerHour));
        Assert.Contains("modified rate of 800 m/h stays", Assert.Single(changed.Notes), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ConflictException>(() => Update().HandleAsync(
            new UpdateCapabilityCommand(added.Id, capability.Version, 1100m, _m, 45, 2m, true, capability.Value.EffectiveFrom, null), CancellationToken.None));

        var current = Capabilities.Find(added.Id)!;
        await Assert.ThrowsAsync<DomainRuleException>(() => Update().HandleAsync(
            new UpdateCapabilityCommand(added.Id, current.Version, 1000m, _kg, 45, 2m, true, current.Value.EffectiveFrom, null), CancellationToken.None));
    }

    [Fact]
    public async Task Ending_a_capability_says_how_many_open_jobs_it_leaves_behind()
    {
        var material = await MaterialAsync();
        var added = await Add().HandleAsync(AddCommand(material, from: HandlerFixture.Now.AddDays(-10)), CancellationToken.None);
        Capabilities.OpenJobs[(_line.Id, material)] = 2;

        var ended = await End().HandleAsync(new EndCapabilityCommand(added.Id, Capabilities.Find(added.Id)!.Version), CancellationToken.None);

        Assert.Equal(HandlerFixture.Now, Capabilities.Find(added.Id)!.Value.EffectiveTo);
        Assert.Contains("2 open jobs", Assert.Single(ended.Notes), StringComparison.Ordinal);
        Assert.Equal(EventTypes.CapabilityEnded, _f.Stores.Events.Appended[^1].Type);
    }
}
