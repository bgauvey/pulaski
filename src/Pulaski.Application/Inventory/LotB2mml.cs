using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Pulaski.Application.Inventory;

/// <summary>A material lot read from B2MML (#72): what a receipt needs, and the lot's properties by their ID.</summary>
public sealed record LotB2mmlRow(
    int Line,
    string LotNumber,
    string MaterialCode,
    decimal? Quantity,
    string? UomCode,
    string? LocationCode,
    DateTimeOffset? ExpiresAt,
    string? SupplierLot,
    IReadOnlyList<(string Id, string Value)> Properties,
    IReadOnlyList<string> Problems);

public sealed record LotB2mmlSource(IReadOnlyList<LotB2mmlRow> Rows, IReadOnlyList<string> Problems);

/// <summary>
/// Reads ISA-95 material lots in B2MML XML (#72): every <c>MaterialLot</c>, whatever wraps it, matched by local name in any namespace.
/// <c>ID</c> is the lot number, <c>MaterialDefinitionID</c> the material, <c>Quantity</c> (<c>QuantityString</c> + <c>UnitOfMeasure</c>)
/// what arrived, <c>StorageLocation</c> the location's code and <c>ExpirationTime</c> the expiry. <c>MaterialLotProperty</c> elements are
/// lot attributes by their <c>ID</c>, except <c>SupplierLot</c> and <c>ExpirationDate</c>, which are read as the receipt's. Pure; deciding
/// what to do with a lot is <see cref="ImportB2mmlLotsCommand"/>'s.
/// </summary>
public static class LotB2mml
{
    /// <summary>The most lots one file takes.</summary>
    public const int MaxRows = 5000;

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>A document with one lot and the parts it reads, to start from.</summary>
    public const string Template = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MaterialInformation xmlns="http://www.mesa.org/xml/B2MML">
          <MaterialLot>
            <ID>L-1001</ID>
            <MaterialDefinitionID>PP-1</MaterialDefinitionID>
            <StorageLocation>WH-RECV</StorageLocation>
            <Quantity><QuantityString>1000</QuantityString><UnitOfMeasure>kg</UnitOfMeasure></Quantity>
            <ExpirationTime>2027-06-30</ExpirationTime>
            <MaterialLotProperty><ID>SupplierLot</ID><Value><ValueString>S-55821</ValueString></Value></MaterialLotProperty>
            <MaterialLotProperty><ID>MFI</ID><Value><ValueString>12.4</ValueString></Value></MaterialLotProperty>
          </MaterialLot>
        </MaterialInformation>

        """;

    public static LotB2mmlSource Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new LotB2mmlSource([], ["The file is empty."]);
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new LotB2mmlSource([], [$"The file isn't well-formed XML: {ex.Message}"]);
        }

        var lots = document.Descendants().Where(e => e.Name.LocalName == "MaterialLot").ToList();
        return lots.Count switch
        {
            0 => new LotB2mmlSource([], ["The file has no MaterialLot elements; B2MML material information has one per lot."]),
            > MaxRows => new LotB2mmlSource([], [$"The file has {lots.Count} lots; import at most {MaxRows} at a time."]),
            _ => new LotB2mmlSource([.. lots.Select(ToRow)], []),
        };
    }

    private static LotB2mmlRow ToRow(XElement lot)
    {
        var problems = new List<string>();
        string? First(XElement parent, string name) => Children(parent, name).FirstOrDefault()?.Value.Trim() is { Length: > 0 } v ? v : null;

        var number = First(lot, "ID") ?? "";
        if (number.Length == 0)
        {
            problems.Add("ID is missing; it is the lot number.");
        }

        var material = First(lot, "MaterialDefinitionID") ?? "";
        if (material.Length == 0)
        {
            problems.Add("MaterialDefinitionID is missing; it names the lot's material.");
        }

        decimal? quantity = null;
        string? unit = null;
        if (Children(lot, "Quantity").FirstOrDefault() is { } q)
        {
            var amount = First(q, "QuantityString") ?? (q.HasElements ? null : q.Value.Trim());
            unit = First(q, "UnitOfMeasure");
            if (amount is not null)
            {
                if (decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                {
                    quantity = d;
                }
                else
                {
                    problems.Add($"Quantity '{amount}' isn't a number.");
                }
            }
        }

        string? supplierLot = null;
        var expiry = First(lot, "ExpirationTime");
        var properties = new List<(string, string)>();
        foreach (var property in Children(lot, "MaterialLotProperty"))
        {
            var id = First(property, "ID") ?? "";
            var value = Children(property, "Value").FirstOrDefault() is { } v ? First(v, "ValueString") ?? (v.HasElements ? "" : v.Value.Trim()) : "";
            switch (Normalize(id))
            {
                case "":
                    problems.Add("A MaterialLotProperty has no ID.");
                    break;
                case "supplierlot" or "vendorlot":
                    supplierLot = value.Length == 0 ? null : value;
                    break;
                case "expirationdate" or "expirytime" or "expirationtime" or "expirydate":
                    expiry ??= value.Length == 0 ? null : value;
                    break;
                default:
                    properties.Add((id, value));
                    break;
            }
        }

        var line = ((IXmlLineInfo)lot).HasLineInfo() ? ((IXmlLineInfo)lot).LineNumber : 0;
        return new LotB2mmlRow(line, number, material, quantity, unit, First(lot, "StorageLocation"), Expiry(expiry, problems), supplierLot, properties, problems);
    }

    // A date alone is midnight UTC on that day; a time without an offset is UTC.
    private static DateTimeOffset? Expiry(string? text, List<string> problems)
    {
        if (text is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return at;
        }

        problems.Add($"ExpirationTime '{text}' isn't a date or an ISO 8601 time.");
        return null;
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string Normalize(string id) => string.Concat(id.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
