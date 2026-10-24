using System.Globalization;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Api.WorkOrders;

/// <summary>What lot, container and location labels say (D-059).</summary>
internal static class InventoryLabels
{
    public static string LotTitle(LotSummaryDto lot) => lot.LotNumber ?? $"{lot.MaterialCode} @ {lot.LocationCode}";

    public static LabelContent ForLot(LotSummaryDto lot, string? timeZone) => new(
        "Lot",
        LotTitle(lot),
        ScanCode.ForLot(lot.LabelNo),
        [
            $"{lot.MaterialCode} · {lot.MaterialName}",
            $"{Qty(lot.Quantity)} {lot.UomCode}",
            .. lot.SupplierLot is { } supplier ? [$"Supplier lot {supplier}"] : Array.Empty<string>(),
            .. lot.ExpiresAt is { } expires ? [$"Expires {Day(expires, timeZone)}"] : Array.Empty<string>(),
            .. lot.ProducedByJobNumber is { } job ? [$"Made by {job}"] : Array.Empty<string>(),
            $"Since {Day(lot.CreatedAt, timeZone)}",
        ]);

    public static LabelContent ForContainer(ContainerDto container) => new(
        container.Kind,
        container.Code,
        ScanCode.ForContainer(container.LabelNo),
        [$"At {container.LocationCode}", $"{container.LotCount} {(container.LotCount == 1 ? "lot" : "lots")} when printed"]);

    public static LabelContent ForLocation(InventoryLocationDto location) => new(
        "Location",
        location.Code,
        ScanCode.ForLocation(location.Code),
        [location.Name]);

    /// <summary>A file name made safe from the label's title.</summary>
    public static string FileName(string title) => string.Concat(title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    private static string Qty(decimal value) => value.ToString("#,0.######", CultureInfo.InvariantCulture);

    private static string Day(DateTimeOffset at, string? timeZone)
    {
        var zone = timeZone is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        return TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
