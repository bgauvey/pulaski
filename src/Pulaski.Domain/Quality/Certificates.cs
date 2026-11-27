namespace Pulaski.Domain.Quality;

/// <summary>How a certificate line came out (#137, D-123).</summary>
public static class CertificateOutcome
{
    /// <summary>Every result conforms.</summary>
    public const string Pass = "pass";

    /// <summary>At least one result is outside the specification.</summary>
    public const string Fail = "fail";

    /// <summary>Nothing was recorded for it.</summary>
    public const string NoResult = "no_result";

    /// <summary>Results were recorded, but none was judged (no limits were in force for them).</summary>
    public const string NotJudged = "not_judged";
}

/// <summary>A result as a certificate needs it: its values (measured) or counts (inspected), and whether it conformed then.</summary>
public sealed record CertificateResult(IReadOnlyList<decimal> Values, int? Inspected, int? Defects, bool? Conforms);

/// <summary>
/// One line of a certificate: a spec item's characteristic and requirement, how many results there were, their spread (measured: mean, lowest,
/// highest of every value; inspected: units inspected and defects), and the outcome.
/// </summary>
public sealed record CertificateLine(
    int Results, decimal? Mean, decimal? Min, decimal? Max, int? Inspected, int? Defects, string Outcome);

/// <summary>Summarizes a lot's results for its certificate (#137): one line per spec item, never hiding a failure. Pure.</summary>
public static class Certificates
{
    public static CertificateLine Summarize(bool measured, IReadOnlyList<CertificateResult> results)
    {
        if (results.Count == 0)
        {
            return new CertificateLine(0, null, null, null, null, null, CertificateOutcome.NoResult);
        }

        var outcome = results.Any(r => r.Conforms == false) ? CertificateOutcome.Fail
            : results.All(r => r.Conforms is null) ? CertificateOutcome.NotJudged
            : CertificateOutcome.Pass;
        if (!measured)
        {
            return new CertificateLine(results.Count, null, null, null, results.Sum(r => r.Inspected ?? 0), results.Sum(r => r.Defects ?? 0), outcome);
        }

        var values = results.SelectMany(r => r.Values).ToList();
        return values.Count == 0
            ? new CertificateLine(results.Count, null, null, null, null, null, outcome)
            : new CertificateLine(results.Count, values.Average(), values.Min(), values.Max(), null, null, outcome);
    }
}
