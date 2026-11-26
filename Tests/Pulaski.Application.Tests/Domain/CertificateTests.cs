using Pulaski.Domain.Quality;

namespace Pulaski.Application.Tests.Domain;

/// <summary>A certificate line summarises a lot's results for a spec item and never hides a failure (#137).</summary>
public class CertificateTests
{
    [Fact]
    public void Measured_results_give_their_mean_and_range_and_fail_when_any_failed()
    {
        var line = Certificates.Summarize(true, [new([3.30m, 3.34m], null, null, true), new([3.28m], null, null, true)]);
        Assert.Equal((2, 3.28m, 3.34m, "pass"), (line.Results, line.Min!.Value, line.Max!.Value, line.Outcome));
        Assert.Equal(3.30667m, decimal.Round(line.Mean!.Value, 5));
        Assert.Equal("fail", Certificates.Summarize(true, [new([3.3m], null, null, true), new([3.5m], null, null, false)]).Outcome);
    }

    [Fact]
    public void Inspected_results_add_up_and_missing_or_unjudged_ones_say_so()
    {
        var inspected = Certificates.Summarize(false, [new([], 50, 0, true), new([], 30, 1, true)]);
        Assert.Equal((80, 1, "pass"), (inspected.Inspected!.Value, inspected.Defects!.Value, inspected.Outcome));
        Assert.Equal("no_result", Certificates.Summarize(true, []).Outcome);
        Assert.Equal("not_judged", Certificates.Summarize(true, [new([1m], null, null, null)]).Outcome);
    }
}
