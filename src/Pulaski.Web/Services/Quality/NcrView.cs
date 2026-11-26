namespace Pulaski.Web.Services.Quality;

/// <summary>How nonconformance reports look on screen (#133). Pure.</summary>
public static class NcrView
{
    public static IReadOnlyList<string> Severities { get; } = ["minor", "major", "critical"];

    public static IReadOnlyList<string> Dispositions { get; } = ["use_as_is", "rework", "scrap", "return_to_supplier", "not_applicable"];

    public static string SeverityChip(string severity) => severity switch
    {
        "critical" => "chip chip-danger",
        "major" => "chip chip-warning",
        _ => "chip",
    };

    public static string StatusChip(string status) => status switch
    {
        "closed" => "chip",
        "acting" => "chip chip-live",
        _ => "chip chip-warning",
    };

    /// <summary>
    /// What carrying out a closed NCR's disposition does next to a lot still in stock (#133b): <c>release</c> a held lot used as is, <c>reject</c>
    /// (write off) a held lot to scrap, <c>hold</c> one to scrap, rework or return that isn't held yet; null when there is nothing to do.
    /// </summary>
    public static string? NextStep(string? disposition, string status, decimal quantity)
    {
        var held = status is "on_hold" or "quarantine";
        if (quantity <= 0 || status is not ("available" or "on_hold" or "quarantine"))
        {
            return null;
        }

        return disposition switch
        {
            "use_as_is" => held ? "release" : null,
            "scrap" => held ? "reject" : "hold",
            "rework" or "return_to_supplier" => held ? null : "hold",
            _ => null,
        };
    }

    /// <summary>The NCR page, from anywhere.</summary>
    public static string Href(Guid id) => $"quality/ncrs/{id}";
}
