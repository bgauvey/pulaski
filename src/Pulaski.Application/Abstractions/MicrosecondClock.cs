namespace Pulaski.Application.Abstractions;

/// <summary>
/// The system clock to whole microseconds, as PostgreSQL keeps time. On Linux the system clock gives 100 ns ticks, so a time worked out
/// from it (a projected start, a stamp returned to the caller) would differ by its last digit from the same time read back from the
/// database. Times from this clock store exactly. Timers and time zones are the system's.
/// </summary>
public sealed class MicrosecondClock : TimeProvider
{
    public static MicrosecondClock Instance { get; } = new();

    public override DateTimeOffset GetUtcNow() => ToMicroseconds(System.GetUtcNow());

    public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;

    public override long TimestampFrequency => System.TimestampFrequency;

    public override long GetTimestamp() => System.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, dueTime, period);

    /// <summary>The time without its sub-microsecond ticks.</summary>
    public static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);
}
