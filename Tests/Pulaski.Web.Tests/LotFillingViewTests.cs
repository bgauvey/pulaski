using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>The job page's words for lots filled by reports (D-104).</summary>
public class LotFillingViewTests
{
    private static JobExecutionDto Job(decimal? size = null, decimal? hours = null, bool shift = false) => new()
    {
        JobId = Guid.NewGuid(),
        JobNumber = "WO-1-10",
        JobType = "production",
        Status = "running",
        SiteId = Guid.NewGuid(),
        LineId = Guid.NewGuid(),
        LineCode = "EXT-01",
        BaseUomCode = "m",
        LotSize = size,
        LotHours = hours,
        LotByShift = shift,
    };

    [Fact]
    public void The_rule_is_said_in_one_sentence_and_absent_when_each_report_makes_its_own_lot()
    {
        Assert.Null(LotFillingView.Rule(Job()));
        Assert.False(LotFillingView.FillsLots(Job()));
        Assert.Equal("On EXT-01 reports fill a lot until it holds 1,000 m, then start the next.", LotFillingView.Rule(Job(1000m)));
        Assert.Equal("On EXT-01 reports fill a lot until it holds 1,000 m, it is 8 h old or the shift changes, then start the next.",
            LotFillingView.Rule(Job(1000m, 8m, true)));
        Assert.Equal("On EXT-01 reports fill a lot until the shift changes, then start the next.", LotFillingView.Rule(Job(shift: true)));
    }

    [Fact]
    public void A_lot_says_how_full_it_is()
    {
        var lot = new FillingLotDto { LotId = Guid.NewGuid(), LotNumber = "WO-1-10-01", Quantity = 700m };
        Assert.Equal("700 of 1,000 m", LotFillingView.Holds(Job(1000m), lot));
        Assert.Equal("700 m", LotFillingView.Holds(Job(hours: 8m), lot));
    }

    [Fact]
    public void The_report_form_says_where_output_with_no_lot_number_goes()
    {
        var lot = new FillingLotDto { LotId = Guid.NewGuid(), LotNumber = "WO-1-10-02", LocationCode = "FG", Quantity = 30m };
        var job = Job(100m) with { FillingLots = [lot] };

        Assert.Equal("Left blank, it goes into lot WO-1-10-02 (30 of 100 m); what doesn't fit starts the next lot.", LotFillingView.Hint(job, "FG"));
        Assert.Equal("Left blank, it starts a new lot, which later reports fill.", LotFillingView.Hint(job, "RM"));
        Assert.Null(LotFillingView.Hint(job, null));
        Assert.Null(LotFillingView.Hint(Job(), "FG"));
    }
}
