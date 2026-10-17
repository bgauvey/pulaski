using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// Balances (D-027, D-031): the one quantity of a material at a location, for a material that
/// isn't lot-tracked or a location whose lots mix. Every arrival at a mixing location ends here.
/// </summary>
internal static class Commingling
{
    /// <summary>
    /// The location's balance of the material, locked; or a new, empty one in <paramref name="status"/>
    /// (not yet stored) when it has none.
    /// </summary>
    public static async Task<(Lot Balance, bool IsNew)> BalanceAsync(
        IInventoryRepository inventory,
        IIdGenerator ids,
        Guid siteId,
        Guid materialId,
        Guid baseUomId,
        Guid locationId,
        string origin,
        CancellationToken cancellationToken,
        string status = LotStatus.Available)
    {
        var existing = await inventory.FindBulkLotForUpdateAsync(materialId, locationId, cancellationToken);
        return existing is { } found
            ? (found.Value, false)
            : (Lot.Create(ids.NewId(), siteId, materialId, null, baseUomId, locationId, isBulk: true, origin) with { Status = status }, true);
    }

    /// <summary>
    /// Merges <paramref name="arrived"/>, a lot just written at a location whose lots mix, into
    /// that location's balance (D-031): the lot goes to zero, the balance takes its stock and the
    /// earlier expiry, and genealogy links the two, so a trace still reaches the lot.
    /// </summary>
    /// <returns>The balance, its new version, and a note saying where the lot went.</returns>
    public static async Task<(Lot Balance, long Version, InventoryWarningDto Note)> MergeArrivalAsync(
        IInventoryRepository inventory,
        IEventLog eventLog,
        IEventFactory events,
        IIdGenerator ids,
        Lot arrived,
        string locationCode,
        string? comment,
        CancellationToken cancellationToken)
    {
        var (balance, isNew) = await BalanceAsync(
            inventory, ids, arrived.SiteId, arrived.MaterialId, arrived.UomId, arrived.LocationId!.Value, arrived.Origin, cancellationToken, arrived.Status);
        var (merged, incoming, emptied, outgoing) = balance.Commingle(arrived);

        var left = events.Create(
            EventTypes.LotMerged, "lot", emptied.Id,
            new { emptied.LotNumber, IntoLotId = merged.Id, Quantity = arrived.Quantity, LocationId = arrived.LocationId, Commingled = true, Comment = comment });
        var joined = events.Create(
            EventTypes.LotMerged, "lot", merged.Id,
            new { FromLotIds = new[] { emptied.Id }, Quantity = arrived.Quantity, QuantityAfter = merged.Quantity, merged.ExpiresAt, Commingled = true, Comment = comment });

        await inventory.UpdateLotAsync(emptied, left.OccurredAt, cancellationToken);
        var version = isNew
            ? await inventory.InsertLotAsync(merged, joined.OccurredAt, cancellationToken)
            : await inventory.UpdateLotAsync(merged, joined.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), emptied.Id, outgoing, emptied.UomId, left.ActorId, Comment: comment, RelatedLotId: merged.Id), left.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), merged.Id, incoming, merged.UomId, joined.ActorId, Comment: comment, RelatedLotId: emptied.Id), joined.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync([new GenealogyEdge(ids.NewId(), emptied.Id, merged.Id, null, GenealogyEdge.Merge)], joined.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(left, cancellationToken);
        await eventLog.AppendAsync(joined, cancellationToken);

        return (merged, version, new InventoryWarningDto("commingled", $"{arrived.Name} joined the balance at {locationCode}, where lots mix."));
    }

    /// <summary>The earlier of two expiries; mixed stock is only as good as its oldest part.</summary>
    public static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) =>
        (a, b) switch
        {
            ({ } x, { } y) => x < y ? x : y,
            _ => a ?? b,
        };
}
