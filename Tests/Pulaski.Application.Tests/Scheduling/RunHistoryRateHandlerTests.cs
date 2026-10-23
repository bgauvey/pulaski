using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Scheduling;

/// <summary>Reviewing and accepting a planning rate from run history (D-045), and going back to the ideal rate.</summary>
public class RunHistoryRateHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _line = Guid.CreateVersion7();
    private readonly Guid _material = Guid.CreateVersion7();
    private readonly Guid _metres;
    private readonly RunStandard _standard;

    public RunHistoryRateHandlerTests()
    {
        _metres = _f.Stores.Units.AddUnit("m", "length", 1m).Id;
        _f.Stores.Units.AddMaterial(_material);
        _standard = _f.Stores.Scheduling.AddRunStandard(
            RunStandard.Create(Guid.CreateVersion7(), _line, _material, _metres, 120m, CalculationBasis.IdealRate, HandlerFixture.Now.AddDays(-30)), version: 3);
        _f.Stores.RunHistory.IdealRates[(_line, _material)] = (120m, _metres);
        _f.Stores.RunHistory.Runs[(_line, _material)] = [.. Enumerable.Range(1, 6).Select(d => new RunHistoryRow(
            Guid.CreateVersion7(), $"J-{d}", 1, HandlerFixture.Now.AddDays(-d).AddHours(-2), HandlerFixture.Now.AddDays(-d), 190m + d, _metres))];
    }

    private GetRunHistoryRateHandler Review() => new(_f.Stores.RunHistory, _f.Stores.Units, _f.Time);

    private AdoptHistoryRateHandler Adopt() =>
        new(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.RunHistory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Time);

    private UseIdealRateHandler Ideal() => new(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.RunHistory, _f.Stores.Events, _f.Events, _f.Time);

    private long Version => _f.Stores.Scheduling.FindRunStandard(_standard.Id)!.Version;

    [Fact]
    public async Task A_reviewed_rate_becomes_the_calculated_rate_and_a_modified_rate_stays_in_force()
    {
        _f.Stores.Scheduling.AddRunStandard(_standard.Modify(110m, "planner", "New die", HandlerFixture.Now.AddDays(-2)), version: 3);

        var review = await Review().HandleAsync(new GetRunHistoryRateQuery(_standard.Id, 90), CancellationToken.None);
        Assert.Equal((6, 12m, 120m, 110m), (review.UsedRuns, review.UsedHours, review.IdealRatePerHour!.Value, review.EffectiveRatePerHour));
        var suggested = review.SuggestedRatePerHour!.Value;

        await Adopt().HandleAsync(new AdoptHistoryRateCommand(_standard.Id, review.Version, 90, suggested), CancellationToken.None);

        var saved = _f.Stores.Scheduling.FindRunStandard(_standard.Id)!.Value;
        Assert.Equal((suggested, CalculationBasis.RunHistory, 110m, RateSource.Modified), (saved.CalculatedRatePerHour, saved.CalculationBasis, saved.EffectiveRatePerHour, saved.RateSource));
        Assert.Equal(EventTypes.RunStandardRecalculated, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task A_rate_that_changed_since_review_or_thin_history_is_refused()
    {
        var review = await Review().HandleAsync(new GetRunHistoryRateQuery(_standard.Id, 90), CancellationToken.None);

        // A new run lands before the planner accepts.
        _f.Stores.RunHistory.Runs[(_line, _material)].Add(new RunHistoryRow(Guid.CreateVersion7(), "J-NEW", 1, HandlerFixture.Now.AddHours(-3), HandlerFixture.Now.AddHours(-1), 200m, _metres));
        await Assert.ThrowsAsync<ConflictException>(() =>
            Adopt().HandleAsync(new AdoptHistoryRateCommand(_standard.Id, Version, 90, review.SuggestedRatePerHour!.Value), CancellationToken.None));

        // Too few runs give no rate to accept; the window is bounded; a stale version is refused.
        _f.Stores.RunHistory.Runs[(_line, _material)].RemoveRange(2, 5);
        await Assert.ThrowsAsync<DomainRuleException>(() => Adopt().HandleAsync(new AdoptHistoryRateCommand(_standard.Id, Version, 90, 100m), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => Review().HandleAsync(new GetRunHistoryRateQuery(_standard.Id, 400), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() => Adopt().HandleAsync(new AdoptHistoryRateCommand(_standard.Id, 1, 90, 100m), CancellationToken.None));
        Assert.Equal(CalculationBasis.IdealRate, _f.Stores.Scheduling.FindRunStandard(_standard.Id)!.Value.CalculationBasis);
    }

    [Fact]
    public async Task Going_back_to_the_ideal_rate_follows_the_capability_again()
    {
        var review = await Review().HandleAsync(new GetRunHistoryRateQuery(_standard.Id, 90), CancellationToken.None);
        await Adopt().HandleAsync(new AdoptHistoryRateCommand(_standard.Id, Version, 90, review.SuggestedRatePerHour!.Value), CancellationToken.None);

        _f.Stores.RunHistory.IdealRates[(_line, _material)] = (130m, _metres);
        await Ideal().HandleAsync(new UseIdealRateCommand(_standard.Id, Version), CancellationToken.None);

        var saved = _f.Stores.Scheduling.FindRunStandard(_standard.Id)!.Value;
        Assert.Equal((130m, CalculationBasis.IdealRate), (saved.CalculatedRatePerHour, saved.CalculationBasis));

        _f.Stores.RunHistory.IdealRates.Remove((_line, _material));
        _f.Stores.Scheduling.AddRunStandard(saved with { CalculationBasis = CalculationBasis.RunHistory }, version: 20);
        await Assert.ThrowsAsync<DomainRuleException>(() => Ideal().HandleAsync(new UseIdealRateCommand(_standard.Id, 20), CancellationToken.None));
    }
}
