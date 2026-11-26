using Pulaski.Domain;
using Pulaski.Domain.Quality;

namespace Pulaski.Application.Tests.Domain;

public sealed class NonconformanceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    private static Nonconformance Raise(string source = NcrSources.Manual, Guid? sourceId = null) =>
        Nonconformance.Raise(Guid.CreateVersion7(), Guid.CreateVersion7(), "NCR-000001", " Thin wall ", null, NcrSeverity.Major, source, sourceId, null, null, "qa", At);

    [Fact]
    public void Raising_needs_a_title_a_severity_and_for_anything_but_a_complaint_or_its_own_a_source()
    {
        var ncr = Raise();
        Assert.Equal(("Thin wall", "", NcrStatus.Open), (ncr.Title, ncr.Description, ncr.Status));
        Assert.Throws<DomainRuleException>(() => Raise(NcrSources.SpcAlert));
        Assert.Equal(NcrSources.SpcAlert, Raise(NcrSources.SpcAlert, Guid.CreateVersion7()).SourceKind);
        Assert.Throws<DomainRuleException>(() => Raise("anything"));
        Assert.Throws<DomainRuleException>(() => Nonconformance.Raise(Guid.CreateVersion7(), Guid.CreateVersion7(), "N", " ", null, NcrSeverity.Minor, NcrSources.Manual, null, null, null, "qa", At));
        Assert.Throws<DomainRuleException>(() => Nonconformance.Raise(Guid.CreateVersion7(), Guid.CreateVersion7(), "N", "T", null, "huge", NcrSources.Manual, null, null, null, "qa", At));
    }

    [Fact]
    public void Investigating_keeps_up_to_five_whys_and_moves_on_to_acting_only_with_a_root_cause()
    {
        var ncr = Raise().Investigate(null, ["Why one", " ", "Why two"], complete: false);
        Assert.Equal((NcrStatus.Investigating, 2), (ncr.Status, ncr.Whys!.Count));
        Assert.Throws<DomainRuleException>(() => ncr.Investigate(null, [], complete: true));
        Assert.Throws<DomainRuleException>(() => ncr.Investigate("Cause", ["1", "2", "3", "4", "5", "6"], complete: false));
        var acting = ncr.Investigate("Die worn", ncr.Whys, complete: true);
        Assert.Equal(NcrStatus.Acting, acting.Status);
        Assert.Equal(NcrStatus.Acting, acting.Investigate("Die worn out", [], complete: false).Status);
    }

    [Fact]
    public void Closing_needs_its_cause_actions_all_finished_a_disposition_and_a_note()
    {
        var acting = Raise().Investigate("Die worn", [], complete: true);
        var action = NcrAction.Add(Guid.CreateVersion7(), acting.Id, NcrActionKinds.Corrective, "Replace the die", null, new DateOnly(2026, 11, 20));
        Assert.True(action.IsOverdue(new DateOnly(2026, 11, 25)));
        Assert.False(action.IsOverdue(new DateOnly(2026, 11, 20)));

        Assert.Throws<DomainRuleException>(() => Raise().Close(NcrDispositions.Scrap, "Done", [action], "qa", At));
        Assert.Throws<DomainRuleException>(() => acting.Close(NcrDispositions.Scrap, "Done", [], "qa", At));
        Assert.Throws<DomainRuleException>(() => acting.Close(NcrDispositions.Scrap, "Done", [action], "qa", At));

        var done = action.Complete("Replaced", "qa", At);
        Assert.False(done.IsOverdue(new DateOnly(2026, 11, 25)));
        Assert.Throws<DomainRuleException>(() => done.Cancel("No", "qa", At));
        Assert.Throws<DomainRuleException>(() => action.Complete(" ", "qa", At));
        Assert.Throws<DomainRuleException>(() => acting.Close("melt", "Done", [done], "qa", At));
        Assert.Throws<DomainRuleException>(() => acting.Close(NcrDispositions.Scrap, null, [done], "qa", At));

        var closed = acting.Close(NcrDispositions.Scrap, " Scrapped ", [done], "qa", At);
        Assert.Equal((NcrStatus.Closed, "Scrapped", At), (closed.Status, closed.ClosingNote, closed.ClosedAt!.Value));
        Assert.Throws<DomainRuleException>(() => closed.Edit("T", "", NcrSeverity.Minor, null));
    }
}
