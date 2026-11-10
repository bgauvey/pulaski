using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services.Scheduling;

namespace Pulaski.Web.Tests;

/// <summary>Planning rates from run history and their proposals, in words (#70).</summary>
public class RateProposalViewTests
{
    [Fact]
    public void The_basis_says_whether_stops_were_taken_out()
    {
        Assert.Equal("from the ideal rate", RateProposalView.Basis("ideal_rate"));
        Assert.Equal("from run history", RateProposalView.Basis("run_history"));
        Assert.Equal("from run history, stops taken out", RateProposalView.Basis("run_history_net"));
        Assert.True(RateProposalView.IsRunHistory("run_history_net"));
        Assert.False(RateProposalView.IsRunHistory("ideal_rate"));
    }

    [Fact]
    public void A_proposal_says_what_it_was_worked_out_from_and_how_far_it_moves()
    {
        var p = new RateProposalDto { UsedRuns = 6, UsedHours = 10.5m, StoppedHours = 1.5m, WindowDays = 90, CalculationBasis = "run_history_net" };
        Assert.Equal("6 runs, 10.5 h running (1.5 h of stops taken out) over 90 days", RateProposalView.Sample(p));
        Assert.Equal("6 runs, 12 h over 90 days", RateProposalView.Sample(p with { CalculationBasis = "run_history", UsedHours = 12m }));
        Assert.Equal("10% faster than", RateProposalView.Change(110m, 100m));
        Assert.Equal("about the same as", RateProposalView.Change(100.2m, 100m));
        Assert.Equal("Accepted without review", RateProposalView.Status("auto_accepted"));
    }
}
