using System.Globalization;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services;

/// <summary>Words for importing ERP work orders from a file (D-066, D-102): checked first (a dry run), then imported order by order.</summary>
public static class ErpOrderImportView
{
    /// <summary>The results that change something when imported (not unchanged or refused).</summary>
    private static readonly string[] Changes = ["created", "updated", "pending", "cancelled"];

    /// <summary>The order the summary counts them in.</summary>
    private static readonly string[] Order = ["created", "updated", "pending", "cancelled", "unchanged", "refused"];

    /// <summary>Why the import can't run yet; null when it can.</summary>
    public static string? Why(ImportWorkOrdersResponse? check, string? externalSystem) =>
        string.IsNullOrWhiteSpace(externalSystem) ? "Name the ERP the orders come from first."
        : check is null ? "Pick a file first: Pulaski checks it, then you import it."
        : !check.DryRun ? "Imported. Pick another file to import more."
        : check.FileProblems.Count > 0 ? "Fix what the check found, then pick the file again."
        : !check.Results.Any(r => Changes.Contains(r.Action)) ? "The file changes nothing."
        : null;

    /// <summary>"Ready: 2 to create, 1 refused (not imported)." or "Imported: 2 created, 1 refused."</summary>
    public static string Summary(ImportWorkOrdersResponse response)
    {
        if (response.FileProblems.Count > 0)
        {
            return string.Join(" ", response.FileProblems);
        }

        var counts = response.Results.GroupBy(r => r.Action).ToDictionary(g => g.Key, g => g.Count());
        var parts = Order
            .Where(counts.ContainsKey)
            .Select(action => $"{counts[action].ToString(CultureInfo.InvariantCulture)} {Action(action, response.DryRun)}");
        return $"{(response.DryRun ? "Ready" : "Imported")}: {string.Join(", ", parts)}.";
    }

    /// <summary>What a result means, as a check says it or as the import did it.</summary>
    public static string Action(string action, bool dryRun) => (action, dryRun) switch
    {
        ("created", true) => "to create",
        ("updated", true) => "to update",
        ("pending", true) => "to wait for a planner",
        ("cancelled", true) => "to cancel",
        ("refused", true) => "refused (not imported)",
        ("pending", false) => "waiting for a planner",
        _ => action,
    };
}
