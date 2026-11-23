using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Inventory;

/// <summary>How a label printer reads on Site settings (#75, #168).</summary>
public static class LabelPrinterView
{
    /// <summary>"Raw TCP to 10.0.0.5:9100", "Files to the 'bartender' drop folder", "BarTender at bt.example.com".</summary>
    public static string Sends(Text l, LabelPrinterDto printer) => printer.Method switch
    {
        "raw_tcp" => l["Raw TCP to {0}:{1}", printer.Host, printer.Port],
        "bartender" => l["BarTender at {0}", Uri.TryCreate(printer.EndpointUrl, UriKind.Absolute, out var uri) ? uri.Authority : printer.EndpointUrl]
            + (printer.BartenderPrinter is { } name ? l[" to {0}", name] : ""),
        _ => l["Files to the '{0}' drop folder", printer.DropFolder],
    };

    /// <summary>The template set, or for BarTender how many documents it names.</summary>
    public static string Templates(Text l, LabelPrinterDto printer) => printer.Method == "bartender"
        ? printer.BtwFiles.Count == 1 ? l["1 BarTender document"] : l["BarTender documents: {0}", printer.BtwFiles.Count]
        : printer.TemplateSet ?? "";

    /// <summary>The operations offered, then any others the printer already names, in that order.</summary>
    public static IReadOnlyList<string> Operations(IReadOnlyList<string> offered, IReadOnlyDictionary<string, string> named) =>
        [.. offered, .. named.Keys.Where(k => !offered.Contains(k)).Order(StringComparer.Ordinal)];

    /// <summary>"Default (any label)", "Lot labels", "Lot labels: produced".</summary>
    public static string OperationName(Text l, string operation) => operation switch
    {
        "default" => l["Default (any label)"],
        _ when operation.Split('.') is [var kind, var what] => l["{0} labels: {1}", Capital(kind), what.Replace('_', ' ')],
        _ => l["{0} labels", Capital(operation)],
    };

    private static string Capital(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
