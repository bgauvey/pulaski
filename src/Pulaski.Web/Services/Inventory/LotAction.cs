using Pulaski.Contracts.Security;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Inventory;

/// <summary>The movements the inventory drawer records (D-027).</summary>
public enum LotAction
{
    Receive,
    Produce,
    Consume,
    WriteUp,
    WriteOff,
    Move,
    Load,
    Hold,
    Release,
    Split,
    Merge,
    Ship,
    Correct,
    Reverse,
}

/// <summary>What the drawer did, for the page to show and reload after.</summary>
public sealed record LotActionResult(Pulaski.Contracts.Inventory.LotTransactionResponse Response, string Message);

public static class LotActions
{
    /// <summary>The permission an action needs (D-037). Consuming and declaring production are reporting into a job.</summary>
    public static string Permission(LotAction action) => action switch
    {
        LotAction.Receive => PermissionCodes.LotReceive,
        LotAction.Produce or LotAction.Consume => PermissionCodes.ProductionReport,
        LotAction.Move or LotAction.Load => PermissionCodes.LotMove,
        LotAction.Hold or LotAction.Release => PermissionCodes.LotHold,
        LotAction.Ship => PermissionCodes.LotShip,
        _ => PermissionCodes.InventoryCorrect,
    };

    /// <summary>The drawer's title for an action.</summary>
    public static string Title(LotAction action) => action switch
    {
        LotAction.Receive => Term.Mark("Receive a lot"),
        LotAction.Produce => Term.Mark("Declare production"),
        LotAction.Consume => Term.Mark("Declare consumption"),
        LotAction.WriteUp => Term.Mark("Write up"),
        LotAction.WriteOff => Term.Mark("Write off"),
        LotAction.Move => Term.Mark("Move"),
        LotAction.Load => Term.Mark("Load at a feed point"),
        LotAction.Hold => Term.Mark("Hold or quarantine"),
        LotAction.Release => Term.Mark("Release"),
        LotAction.Split => Term.Mark("Split"),
        LotAction.Merge => Term.Mark("Merge lots into this one"),
        LotAction.Ship => Term.Mark("Ship"),
        LotAction.Correct => Term.Mark("Correct details"),
        LotAction.Reverse => Term.Mark("Reverse a movement"),
        _ => action.ToString(),
    };
}

/// <summary>
/// Why a lot's action can't be taken now (#47), for the button's reason; null when it can. Only the lot's state: whether the user may is
/// decided elsewhere, and an action they may never take isn't shown at all.
/// </summary>
public static class LotActionAvailability
{
    public static string? Why(LotAction action, string status, decimal quantity, bool expired)
    {
        var gone = status switch
        {
            "available" or "on_hold" or "quarantine" => null,
            "consumed" => Term.Mark("The lot is used up."),
            "shipped" => Term.Mark("The lot has been shipped."),
            "scrapped" => Term.Mark("The lot was written off."),
            "cancelled" => Term.Mark("The lot's receipt was reversed."),
            "planned" or "in_production" => Term.Mark("The lot isn't made yet."),
            _ => Term.Mark("The lot isn't in stock."),
        };
        var expiredWhy = Term.Mark("The lot has expired; correct its expiry first if quality extended it.");
        var empty = Term.Mark("None of the lot is left.");
        var notUsable = gone
            ?? (status == "on_hold" ? Term.Mark("The lot is on hold; release it first.")
            : status == "quarantine" ? Term.Mark("The lot is in quarantine; release it first.")
            : quantity <= 0 ? empty
            : expired ? expiredWhy
            : null);
        return action switch
        {
            LotAction.Consume or LotAction.Ship => notUsable,
            LotAction.Move or LotAction.Load or LotAction.Hold => gone,
            LotAction.Split or LotAction.WriteOff => gone ?? (quantity <= 0 ? empty : null),
            LotAction.Merge => gone ?? (expired ? expiredWhy : null),
            _ => null,
        };
    }
}
