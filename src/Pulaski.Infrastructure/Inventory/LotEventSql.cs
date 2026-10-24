namespace Pulaski.Infrastructure.Inventory;

/// <summary>SQL shared by the queries that read lot movements.</summary>
internal static class LotEventSql
{
    /// <summary>
    /// A movement of <c>ts.lot_event e</c> that no reversal undid (D-063): a reversed declaration counts as never made, so the job's
    /// consumption, its produced lots and its genealogy leave it out.
    /// </summary>
    public const string NotReversed = "NOT EXISTS (SELECT 1 FROM ts.lot_event rv WHERE rv.lot_id = e.lot_id AND rv.reverses_event_id = e.id)";
}
