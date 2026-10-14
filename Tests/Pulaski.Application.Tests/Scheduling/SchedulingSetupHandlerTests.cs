using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Scheduling;

public class SchedulingSetupHandlerTests
{
    private readonly HandlerFixture _f = new();

    private ModifyRunStandardHandler Modify() => new(
        _f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.Events, _f.Events, new FixedRequestContext(), _f.Time);

    private ClearRunStandardHandler Clear() => new(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.Events, _f.Events);

    private SetSchedulingPolicyHandler SetPolicy() =>
        new(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.Equipment, _f.Stores.Events, _f.Events);

    private RunStandard Standard() => _f.Stores.Scheduling.AddRunStandard(
        RunStandard.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            900m, CalculationBasis.IdealRate, HandlerFixture.Now.AddDays(-1)),
        version: 4);

    [Fact]
    public async Task Modify_sets_the_gold_standard_and_records_who()
    {
        var standard = Standard();

        var result = await Modify().HandleAsync(
            new ModifyRunStandardCommand(standard.Id, 4, 750m, "  Screen wear  "), CancellationToken.None);

        var row = _f.Stores.Scheduling.FindRunStandard(standard.Id)!;
        Assert.Equal(5, result.Version);
        Assert.Equal((750m, RateSource.Modified, "test-user", "Screen wear"), (row.Value.EffectiveRatePerHour, row.Value.RateSource, row.Value.ModifiedBy!, row.Value.ModifiedNote!));
        Assert.Equal(900m, row.Value.CalculatedRatePerHour);
        Assert.Equal(EventTypes.RunStandardModified, Assert.Single(_f.Stores.Events.Appended).Type);
    }

    [Fact]
    public async Task Clear_returns_to_the_calculated_rate()
    {
        var standard = Standard();
        await Modify().HandleAsync(new ModifyRunStandardCommand(standard.Id, 4, 750m, null), CancellationToken.None);

        await Clear().HandleAsync(new ClearRunStandardCommand(standard.Id, 5, "Screen replaced"), CancellationToken.None);

        var row = _f.Stores.Scheduling.FindRunStandard(standard.Id)!.Value;
        Assert.Equal((900m, RateSource.Calculated), (row.EffectiveRatePerHour, row.RateSource));
        var evt = _f.Stores.Events.Appended[^1];
        Assert.Equal((EventTypes.RunStandardModificationCleared, "Screen replaced"), (evt.Type, evt.Reason));
    }

    [Fact]
    public async Task Run_standard_rules_conflicts_and_validation()
    {
        var standard = Standard();

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Clear().HandleAsync(new ClearRunStandardCommand(standard.Id, 4, null), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() =>
            Modify().HandleAsync(new ModifyRunStandardCommand(standard.Id, 3, 750m, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            Modify().HandleAsync(new ModifyRunStandardCommand(standard.Id, 4, 0m, null), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Modify().HandleAsync(new ModifyRunStandardCommand(Guid.CreateVersion7(), 1, 750m, null), CancellationToken.None));
        Assert.Empty(_f.Stores.Events.Appended);
    }

    [Fact]
    public async Task Policy_is_created_from_version_zero_then_updated()
    {
        var (_, site, _) = _f.Stores.Equipment.AddHierarchy();

        var created = await SetPolicy().HandleAsync(
            new SetSchedulingPolicyCommand(site.Id, 0, ChangeoverMode.Scheduler), CancellationToken.None);
        var updated = await SetPolicy().HandleAsync(
            new SetSchedulingPolicyCommand(site.Id, created.Version, ChangeoverMode.Auto), CancellationToken.None);

        Assert.Equal((ChangeoverMode.Auto, updated.Version), (_f.Stores.Scheduling.FindPolicy(site.Id)!.Value, _f.Stores.Scheduling.FindPolicy(site.Id)!.Version));
        Assert.All(_f.Stores.Events.Appended, e => Assert.Equal((EventTypes.SchedulingPolicyChanged, site.Id), (e.Type, e.Subject)));
    }

    [Fact]
    public async Task Policy_refuses_stale_versions_lines_and_unknown_modes()
    {
        var (_, site, line) = _f.Stores.Equipment.AddHierarchy();

        await Assert.ThrowsAsync<ConflictException>(() => SetPolicy().HandleAsync(
            new SetSchedulingPolicyCommand(site.Id, 3, ChangeoverMode.Scheduler), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => SetPolicy().HandleAsync(
            new SetSchedulingPolicyCommand(line.Id, 0, ChangeoverMode.Scheduler), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => SetPolicy().HandleAsync(
            new SetSchedulingPolicyCommand(Guid.CreateVersion7(), 0, ChangeoverMode.Scheduler), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => SetPolicy().HandleAsync(
            new SetSchedulingPolicyCommand(site.Id, 0, "manual"), CancellationToken.None));
    }
}
