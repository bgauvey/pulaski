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
}

/// <summary>What the drawer did, for the page to show and reload after.</summary>
public sealed record LotActionResult(Pulaski.Contracts.Inventory.LotTransactionResponse Response, string Message);

public static class LotActions
{
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
        _ => action.ToString(),
    };
}
