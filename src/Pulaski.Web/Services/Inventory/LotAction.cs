using Pulaski.Contracts.Security;

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
        LotAction.Receive => "Receive a lot",
        LotAction.Produce => "Declare production",
        LotAction.Consume => "Declare consumption",
        LotAction.WriteUp => "Write up",
        LotAction.WriteOff => "Write off",
        LotAction.Move => "Move",
        LotAction.Load => "Load at a feed point",
        LotAction.Hold => "Hold or quarantine",
        LotAction.Release => "Release",
        LotAction.Split => "Split",
        LotAction.Merge => "Merge lots into this one",
        LotAction.Ship => "Ship",
        LotAction.Correct => "Correct details",
        LotAction.Reverse => "Reverse a movement",
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
            "consumed" => "The lot is used up.",
            "shipped" => "The lot has been shipped.",
            "scrapped" => "The lot was written off.",
            "cancelled" => "The lot's receipt was reversed.",
            "planned" or "in_production" => "The lot isn't made yet.",
            _ => "The lot isn't in stock.",
        };
        const string Expired = "The lot has expired; correct its expiry first if quality extended it.";
        const string Empty = "None of the lot is left.";
        var notUsable = gone
            ?? (status == "on_hold" ? "The lot is on hold; release it first."
            : status == "quarantine" ? "The lot is in quarantine; release it first."
            : quantity <= 0 ? Empty
            : expired ? Expired
            : null);
        return action switch
        {
            LotAction.Consume or LotAction.Ship => notUsable,
            LotAction.Move or LotAction.Load or LotAction.Hold => gone,
            LotAction.Split or LotAction.WriteOff => gone ?? (quantity <= 0 ? Empty : null),
            LotAction.Merge => gone ?? (expired ? Expired : null),
            _ => null,
        };
    }
}
