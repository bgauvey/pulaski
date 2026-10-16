namespace Pulaski.Domain.Scheduling;

/// <summary>
/// Fractional ranks for <c>core.job.sequence_rank</c> (<c>numeric(20,10)</c>), so moving a job
/// writes one row (D-014). A new rank sits halfway between its neighbours; when ten decimal places
/// leave no room, the line is renumbered and the move is retried.
/// </summary>
public static class SequenceRank
{
    public const int Scale = 10;

    /// <summary>
    /// A rank strictly between <paramref name="before"/> and <paramref name="after"/>, where null
    /// means the start or end of the line. Null when no such rank fits the column's scale.
    /// </summary>
    public static decimal? Between(decimal? before, decimal? after) =>
        (before, after) switch
        {
            (null, null) => 1m,
            ({ } b, null) => decimal.Floor(b) + 1m,
            (null, { } a) => decimal.Ceiling(a) - 1m,
            ({ } b, { } a) => Midpoint(b, a),
        };

    private static decimal? Midpoint(decimal before, decimal after)
    {
        var mid = Math.Round((before + after) / 2m, Scale, MidpointRounding.ToZero);
        return mid > before && mid < after ? mid : null;
    }
}
