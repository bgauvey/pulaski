namespace Pulaski.Domain.MachineData;

/// <summary>An alarm or warning a tag raised and the machine hasn't cleared yet (#26).</summary>
public sealed record OpenAlarm(Guid Id, DateTimeOffset RaisedAt, int Number);

/// <summary>
/// Alarms and warnings from code-valued tags (#26): a tag's value is the active alarm number, as PackML <c>Admin.Alarm[].ID</c> and
/// <c>Admin.Warning[].ID</c> carry it, and 0 means none. They are history of their own, apart from the machine's state and its stops.
/// </summary>
public static class AlarmRules
{
    /// <summary>
    /// What a new reading of a tag does: a different number clears the one open (if any) and raises the new one, 0 clears, the same
    /// number changes nothing. A reading that isn't good changes nothing either: losing the signal doesn't mean the alarm cleared.
    /// </summary>
    /// <returns>The alarm cleared, with when (never before it was raised), and the number raised, if any.</returns>
    public static (OpenAlarm Alarm, DateTimeOffset ClearedAt)? Cleared(OpenAlarm? open, int? reading, bool good, DateTimeOffset at) =>
        good && reading is { } number && open is not null && open.Number != number
            ? (open, at < open.RaisedAt ? open.RaisedAt : at)
            : null;

    public static int? Raised(OpenAlarm? open, int? reading, bool good) =>
        good && reading is { } number && number != 0 && open?.Number != number ? number : null;

    /// <summary>A tag's reading as an alarm number: the whole part of the value; none for a missing value.</summary>
    public static int? Number(decimal? value) => value is { } v && v >= int.MinValue && v <= int.MaxValue ? decimal.ToInt32(decimal.Truncate(v)) : null;
}
