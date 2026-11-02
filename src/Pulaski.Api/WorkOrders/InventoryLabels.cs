using System.Globalization;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Api.WorkOrders;

/// <summary>
/// What lot, container and location labels say (D-059), and in a GS1 format (#75) the elements their GS1 barcode carries: a lot (01) GTIN,
/// (17) expiry, a net weight and (10) lot; a container (00) SSCC. GS1 needs the GTIN or SSCC, so without one a label stays Pulaski's.
/// </summary>
internal static class InventoryLabels
{
    public static string LotTitle(LotSummaryDto lot) => lot.LotNumber ?? $"{lot.MaterialCode} @ {lot.LocationCode}";

    public static LabelContent ForLot(LotSummaryDto lot, string? timeZone, string format = LabelFormats.Pulaski)
    {
        var expiry = lot.ExpiresAt is { } expires ? Day(expires, timeZone) : (DateOnly?)null;
        var lines = new List<string>
        {
            $"{lot.MaterialCode} · {lot.MaterialName}",
            $"{Qty(lot.Quantity)} {lot.UomCode}",
        };
        lines.AddRange(lot.SupplierLot is { } supplier ? [$"Supplier lot {supplier}"] : []);
        lines.AddRange(expiry is { } day ? [$"Expires {day:yyyy-MM-dd}"] : []);
        lines.AddRange(lot.ProducedByJobNumber is { } job ? [$"Made by {job}"] : []);
        lines.Add($"Since {Day(lot.CreatedAt, timeZone):yyyy-MM-dd}");

        var gs1 = format != LabelFormats.Pulaski ? LotElements(lot, expiry) : null;
        return new LabelContent("Lot", LotTitle(lot), ScanCode.ForLot(lot.LabelNo), lines, gs1 is null ? LabelFormats.Pulaski : format, gs1)
        {
            Fields = new Dictionary<string, string?>
            {
                ["LotNumber"] = lot.LotNumber,
                ["MaterialCode"] = lot.MaterialCode,
                ["MaterialName"] = lot.MaterialName,
                ["Quantity"] = Qty(lot.Quantity),
                ["Uom"] = lot.UomCode,
                ["SupplierLot"] = lot.SupplierLot,
                ["Expiry"] = expiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["Gtin"] = lot.MaterialGtin,
                ["Location"] = lot.LocationCode,
                ["Container"] = lot.ContainerCode,
                ["Job"] = lot.ProducedByJobNumber,
            },
        };
    }

    public static LabelContent ForContainer(ContainerDto container, string format = LabelFormats.Pulaski)
    {
        IReadOnlyList<Gs1Element>? gs1 = format != LabelFormats.Pulaski && container.Sscc is { } sscc ? [new Gs1Element("00", sscc)] : null;
        return new LabelContent(
            container.Kind,
            container.Code,
            ScanCode.ForContainer(container.LabelNo),
            [
                $"At {container.LocationCode}{(container.ParentContainerCode is { } parent ? $", in {parent}" : "")}",
                $"{container.LotCount} {(container.LotCount == 1 ? "lot" : "lots")}{(container.ContainerCount > 0 ? $" and {container.ContainerCount} {(container.ContainerCount == 1 ? "container" : "containers")}" : "")} when printed",
            ],
            gs1 is null ? LabelFormats.Pulaski : format,
            gs1)
        {
            Fields = new Dictionary<string, string?>
            {
                ["ContainerCode"] = container.Code,
                ["Location"] = container.LocationCode,
                ["Lots"] = container.LotCount.ToString(CultureInfo.InvariantCulture),
                ["Sscc"] = container.Sscc,
                ["ParentContainer"] = container.ParentContainerCode,
                ["Containers"] = container.ContainerCount.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    public static LabelContent ForLocation(InventoryLocationDto location) => new(
        "Location",
        location.Code,
        ScanCode.ForLocation(location.Code),
        [location.Name])
    {
        Fields = new Dictionary<string, string?> { ["LocationCode"] = location.Code, ["LocationName"] = location.Name },
    };

    /// <summary>A file name made safe from the label's title.</summary>
    public static string FileName(string title) => string.Concat(title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    /// <summary>
    /// A lot's GS1 elements, fixed-length first so only (10) at the end is variable: (01) GTIN, (17) expiry, (310n)/(320n) net weight for a
    /// lot kept in kg or lb, (10) lot. Null when it has no GTIN or its number can't go in (10).
    /// </summary>
    public static IReadOnlyList<Gs1Element>? LotElements(LotSummaryDto lot, DateOnly? expiry)
    {
        if (lot.MaterialGtin is not { } gtin || lot.LotNumber is not { } number || number.Length > Gs1.MaxLot || !Gs1.IsCset82(number))
        {
            return null;
        }

        var elements = new List<Gs1Element> { new("01", gtin) };
        if (expiry is { } day)
        {
            elements.Add(new Gs1Element("17", Gs1.DateValue(day)));
        }

        var unit = lot.UomCode.ToLowerInvariant();
        if (unit is "kg" or "lb" && Gs1.NetWeight(lot.Quantity, unit == "lb") is { } weight)
        {
            elements.Add(weight);
        }

        elements.Add(new Gs1Element("10", number));
        return elements;
    }

    private static string Qty(decimal value) => value.ToString("#,0.######", CultureInfo.InvariantCulture);

    private static DateOnly Day(DateTimeOffset at, string? timeZone)
    {
        var zone = timeZone is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
    }
}
