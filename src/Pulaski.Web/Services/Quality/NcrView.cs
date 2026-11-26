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

    /// <summary>The NCR page, from anywhere.</summary>
    public static string Href(Guid id) => $"quality/ncrs/{id}";
}
