using Pulaski.Contracts.WorkOrders;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services;

/// <summary>Words for importing ERP work orders from a file (D-066, D-102): checked first (a dry run), then imported order by order.</summary>
public static class ErpOrderImportView
{
    /// <summary>The results that change something when imported (not unchanged or refused).</summary>
    private static readonly string[] Changes = ["created", "updated", "pending", "cancelled"];

    /// <summary>The order the summary counts them in.</summary>
    private static readonly string[] Order = ["created", "updated", "pending", "cancelled", "unchanged", "refused"];

    /// <summary>Why the import can't run yet; null when it can.</summary>
    public static string? Why(Text l, ImportWorkOrdersResponse? check, string? externalSystem) =>
        string.IsNullOrWhiteSpace(externalSystem) ? l["Name the ERP the orders come from first."]
        : check is null ? l["Pick a file first: Pulaski checks it, then you import it."]
        : !check.DryRun ? l["Imported. Pick another file to import more."]
        : check.FileProblems.Count > 0 ? l["Fix what the check found, then pick the file again."]
        : !check.Results.Any(r => Changes.Contains(r.Action)) ? l["The file changes nothing."]
        : null;

    /// <summary>"Ready: 2 to create, 1 refused (not imported)." or "Imported: 2 created, 1 refused."</summary>
    public static string Summary(Text l, ImportWorkOrdersResponse response)
    {
        if (response.FileProblems.Count > 0)
        {
            return string.Join(" ", l.Notes(response.FileProblems));
        }

        var counts = response.Results.GroupBy(r => r.Action).ToDictionary(g => g.Key, g => g.Count());
        var parts = string.Join(", ", Order.Where(counts.ContainsKey).Select(action => Count(l, action, counts[action], response.DryRun)));
        return response.DryRun ? l["Ready: {0}.", parts] : l["Imported: {0}.", parts];
    }

    /// <summary>What a result means, as a check says it or as the import did it.</summary>
    public static string Action(Text l, string action, bool dryRun) => (action, dryRun) switch
    {
        ("created", true) => l["to create"],
        ("updated", true) => l["to update"],
        ("pending", true) => l["to wait for a planner"],
        ("cancelled", true) => l["to cancel"],
        ("refused", true) => l["refused (not imported)"],
        ("pending", false) => l["waiting for a planner"],
        _ => l.Word(action),
    };

    private static string Count(Text l, string action, int n, bool dryRun) => (action, dryRun) switch
    {
        ("created", true) => l["{0} to create", n],
        ("updated", true) => l["{0} to update", n],
        ("pending", true) => l["{0} to wait for a planner", n],
        ("cancelled", true) => l["{0} to cancel", n],
        ("refused", true) => l["{0} refused (not imported)", n],
        ("pending", false) => l["{0} waiting for a planner", n],
        ("created", false) => l["{0} created", n],
        ("updated", false) => l["{0} updated", n],
        ("cancelled", false) => l["{0} canceled", n],
        ("refused", false) => l["{0} refused", n],
        _ => l["{0} unchanged", n],
    };
}
