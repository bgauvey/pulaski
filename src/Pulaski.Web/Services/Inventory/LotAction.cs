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
        LotAction.Move => PermissionCodes.LotMove,
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
