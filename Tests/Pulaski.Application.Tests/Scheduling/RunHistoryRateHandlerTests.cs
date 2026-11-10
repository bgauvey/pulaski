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

    private GetRunHistoryRateHandler Review() => new(_f.Stores.RunHistory, _f.Stores.Units, _f.Stores.RateRefresh, _f.Time);

    private AdoptHistoryRateHandler Adopt() =>
        new(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.RateRefresh, _f.Stores.RunHistory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Time);

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

    private RateRefreshRunner Runner() => new(
        _f.Stores.UnitOfWork, _f.Stores.RateRefresh, _f.Stores.Scheduling, _f.Stores.RunHistory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private RateRefreshSettings Scheduled(bool autoAccept = false) =>
        RateRefreshSettings.Default(_f.Stores.RunHistory.SiteId) with { IsScheduled = true, AutoAccept = autoAccept };

    [Fact]
    public async Task Stops_can_only_come_out_on_a_line_that_records_machine_state()
    {
        var review = await Review().HandleAsync(new GetRunHistoryRateQuery(_standard.Id, 90, TakeOutStops: true), CancellationToken.None);

        Assert.False(review.StopsTakenOut);
        Assert.StartsWith("EXT-01 doesn't record machine state", review.StopsNote, StringComparison.Ordinal);
        Assert.Equal(CalculationBasis.RunHistory, review.SuggestedBasis);
    }

    [Fact]
    public async Task The_scheduled_recalculation_proposes_and_a_planner_accepts_the_first_history_rate()
    {
        _f.Stores.RateRefresh.StandardsOfSite[_f.Stores.RunHistory.SiteId] = [_standard.Id];
        _f.Stores.RateRefresh.Set(Scheduled(autoAccept: true));

        // Due, and the history (96.75/h) is far from the ideal rate (120/h): proposed, but a rate still on the ideal rate is reviewed.
        Assert.Equal(1, await Runner().RefreshDueAsync(CancellationToken.None));
        var proposal = Assert.Single(_f.Stores.RateRefresh.Proposals);
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Equal(96.75m, proposal.ProposedRatePerHour);
        Assert.StartsWith("The planning rate is still the ideal rate", proposal.ReviewReason, StringComparison.Ordinal);
        Assert.Equal(CalculationBasis.IdealRate, _f.Stores.Scheduling.FindRunStandard(_standard.Id)!.Value.CalculationBasis);

        // Not due again for a week; the same proposal isn't made twice.
        Assert.Equal(0, await Runner().RefreshDueAsync(CancellationToken.None));
        await Runner().RefreshSiteAsync(Scheduled(autoAccept: true), CancellationToken.None);
        Assert.Single(_f.Stores.RateRefresh.Proposals);

        var review = await Review().HandleAsync(new GetRunHistoryRateQuery(_standard.Id, 90), CancellationToken.None);
        Assert.Equal(proposal.Id, review.Proposal!.Id);
        await new AcceptRateProposalHandler(
                _f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.RateRefresh, _f.Stores.RunHistory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Time)
            .HandleAsync(new AcceptRateProposalCommand(_standard.Id, Version, proposal.Id), CancellationToken.None);

        var saved = _f.Stores.Scheduling.FindRunStandard(_standard.Id)!.Value;
        Assert.Equal((96.75m, CalculationBasis.RunHistory), (saved.CalculatedRatePerHour, saved.CalculationBasis));
        Assert.Equal(ProposalStatus.Accepted, Assert.Single(_f.Stores.RateRefresh.Proposals).Status);
    }

    [Fact]
    public async Task Small_changes_to_a_history_rate_are_accepted_when_the_site_opts_in_and_a_modified_rate_stays()
    {
        _f.Stores.Scheduling.AddRunStandard(
            _standard.Recalculate(90m, CalculationBasis.RunHistory, HandlerFixture.Now.AddDays(-10)).Modify(110m, "planner", "New die", HandlerFixture.Now.AddDays(-2)), version: 4);
        _f.Stores.RateRefresh.StandardsOfSite[_f.Stores.RunHistory.SiteId] = [_standard.Id];

        // Reviewed: the site doesn't opt in.
        await Runner().RefreshSiteAsync(Scheduled(), CancellationToken.None);
        var first = Assert.Single(_f.Stores.RateRefresh.Proposals);
        Assert.Equal("The site reviews every proposal.", first.ReviewReason);

        // Opted in: about 97 is within 10% of 90, so the newer proposal replaces the pending one and is accepted as the system.
        _f.Stores.RunHistory.Runs[(_line, _material)].Add(new RunHistoryRow(Guid.CreateVersion7(), "J-7", 1, HandlerFixture.Now.AddDays(-8).AddHours(-2), HandlerFixture.Now.AddDays(-8), 196m, _metres));
        await Runner().RefreshSiteAsync(Scheduled(autoAccept: true), CancellationToken.None);
        Assert.Equal([ProposalStatus.Superseded, ProposalStatus.AutoAccepted], _f.Stores.RateRefresh.Proposals.Select(p => p.Status));

        var saved = _f.Stores.Scheduling.FindRunStandard(_standard.Id)!.Value;
        Assert.Equal(_f.Stores.RateRefresh.Proposals[1].ProposedRatePerHour, saved.CalculatedRatePerHour);
        Assert.Equal(110m, saved.EffectiveRatePerHour);
    }

    [Fact]
    public async Task A_rejected_proposal_leaves_the_rate_and_a_decided_one_cant_be_accepted()
    {
        _f.Stores.RateRefresh.StandardsOfSite[_f.Stores.RunHistory.SiteId] = [_standard.Id];
        await Runner().RefreshSiteAsync(Scheduled(), CancellationToken.None);
        var proposal = Assert.Single(_f.Stores.RateRefresh.Proposals);

        await new RejectRateProposalHandler(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.RateRefresh, _f.Stores.Events, _f.Events, _f.Time)
            .HandleAsync(new RejectRateProposalCommand(_standard.Id, proposal.Id, "Trial runs"), CancellationToken.None);
        Assert.Equal(ProposalStatus.Rejected, Assert.Single(_f.Stores.RateRefresh.Proposals).Status);
        Assert.Equal(120m, _f.Stores.Scheduling.FindRunStandard(_standard.Id)!.Value.CalculatedRatePerHour);

        await Assert.ThrowsAsync<ConflictException>(() => new AcceptRateProposalHandler(
                _f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.RateRefresh, _f.Stores.RunHistory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Time)
            .HandleAsync(new AcceptRateProposalCommand(_standard.Id, Version, proposal.Id), CancellationToken.None));
    }
}
