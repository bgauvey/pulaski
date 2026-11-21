using Pulaski.Contracts.Inventory;

namespace Pulaski.Web.Services.Inventory;

/// <summary>How a label printer reads on Site settings (#75, #168).</summary>
public static class LabelPrinterView
{
    /// <summary>"Raw TCP to 10.0.0.5:9100", "Files to the 'bartender' drop folder", "BarTender at bt.example.com".</summary>
    public static string Sends(LabelPrinterDto printer) => printer.Method switch
    {
        "raw_tcp" => $"Raw TCP to {printer.Host}:{printer.Port}",
        "bartender" => $"BarTender at {(Uri.TryCreate(printer.EndpointUrl, UriKind.Absolute, out var uri) ? uri.Authority : printer.EndpointUrl)}"
            + (printer.BartenderPrinter is { } name ? $" to {name}" : ""),
        _ => $"Files to the '{printer.DropFolder}' drop folder",
    };

    /// <summary>The template set, or for BarTender how many documents it names.</summary>
    public static string Templates(LabelPrinterDto printer) => printer.Method == "bartender"
        ? printer.BtwFiles.Count == 1 ? "1 BarTender document" : $"{printer.BtwFiles.Count} BarTender documents"
        : printer.TemplateSet ?? "";

    /// <summary>The operations offered, then any others the printer already names, in that order.</summary>
    public static IReadOnlyList<string> Operations(IReadOnlyList<string> offered, IReadOnlyDictionary<string, string> named) =>
        [.. offered, .. named.Keys.Where(k => !offered.Contains(k)).Order(StringComparer.Ordinal)];

    /// <summary>"Default (any label)", "Lot labels", "Lot labels: produced".</summary>
    public static string OperationName(string operation) => operation switch
    {
        "default" => "Default (any label)",
        _ when operation.Split('.') is [var kind, var what] => $"{Capital(kind)} labels: {what.Replace('_', ' ')}",
        _ => $"{Capital(operation)} labels",
    };

    private static string Capital(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
