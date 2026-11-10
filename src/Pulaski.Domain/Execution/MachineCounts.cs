namespace Pulaski.Domain.Execution;

/// <summary>
/// Production from a line's machine counters (#27, D-097). The counter's processed count is everything made, the defective count the
/// scrapped part of it, both already scaled by their tags; one count is <c>unitsPerCount</c> of the material's base unit on that line. A report
/// from the machine records how far into the run's counts it reaches, so the next one starts after it and nothing is counted twice.
/// </summary>
public static class MachineCounts
{
    /// <summary>Good and scrap in the material's base unit; a null <paramref name="unitsPerCount"/> is one.</summary>
    public static (decimal Good, decimal Scrap) Output(decimal processed, decimal defective, decimal? unitsPerCount)
    {
        var perCount = unitsPerCount ?? 1m;
        var scrap = Math.Max(0m, defective);
        return (Math.Max(0m, processed - scrap) * perCount, scrap * perCount);
    }

    /// <summary>A report covering the run's machine counts up to <paramref name="through"/> may follow those already reported.</summary>
    /// <exception cref="DomainRuleException">Its counts are in another report of the run, or aren't from inside the run.</exception>
    public static void EnsureCovers(DateTimeOffset through, DateTimeOffset? coveredThrough, DateTimeOffset runStart, DateTimeOffset runEnd)
    {
        if (coveredThrough is { } covered && through <= covered)
        {
            throw new DomainRuleException("The machine's counts up to then are in another report of this run already. Take the counts again to report what it counted since.");
        }

        if (through < runStart || through > runEnd)
        {
            throw new DomainRuleException("Machine counts are reported from inside their run.");
        }
    }
}
