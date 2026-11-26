using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Inventory;

/// <summary>How the actions a scan offers read (#135). Pure.</summary>
public static class ScanActionView
{
    /// <summary>"Consume 1,000 kg of R-1 into J-1", "Load R-1 at HOP-1", "Start J-2 on EXT-01"…</summary>
    public static string Title(Text l, ScanActionDto a) => a.Kind switch
    {
        "consume" => l["Consume {0} {1} of {2} into {3}", InventoryView.Qty(a.Quantity ?? 0), a.UomCode ?? "", a.LotLabel ?? "", a.JobNumber ?? ""],
        "load" => l["Load {0} at {1}", a.LotLabel ?? "", a.ToCode ?? ""],
        "move" => l["Move {0} to {1}", a.LotLabel ?? "", a.ToCode ?? ""],
        "move_container" => l["Move {0} and what it holds to {1}", a.ContainerCode ?? "", a.ToCode ?? ""],
        "start" => l["Start {0} on {1}", a.JobNumber ?? "", a.ToCode ?? ""],
        _ => a.Kind,
    };

    /// <summary>What was done, for the message after confirming.</summary>
    public static string Done(Text l, ScanActionDto a, decimal? quantity) => a.Kind switch
    {
        "consume" => l["{0} {1} of {2} consumed into {3}.", InventoryView.Qty(quantity ?? a.Quantity ?? 0), a.UomCode ?? "", a.LotLabel ?? "", a.JobNumber ?? ""],
        "load" => l["{0} loaded at {1}.", a.LotLabel ?? "", a.ToCode ?? ""],
        "move" => l["{0} moved to {1}.", a.LotLabel ?? "", a.ToCode ?? ""],
        "move_container" => l["{0} moved to {1}.", a.ContainerCode ?? "", a.ToCode ?? ""],
        "start" => l["{0} started on {1}.", a.JobNumber ?? "", a.ToCode ?? ""],
        _ => a.Kind,
    };

    /// <summary>The actions the user may take at the site, the likely one first, as the API ordered them.</summary>
    public static IReadOnlyList<ScanActionDto> Allowed(IReadOnlyList<ScanActionDto> actions, Func<string, bool> may) => [.. actions.Where(a => may(a.Permission))];

    /// <summary>Where a device keeps the line it is working at, per site.</summary>
    public static string StorageKey(Guid siteId) => $"pulaski.scan.line.{siteId:N}";
}
