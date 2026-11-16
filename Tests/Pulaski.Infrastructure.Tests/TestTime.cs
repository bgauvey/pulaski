using Pulaski.Application.Abstractions;

namespace Pulaski.Infrastructure.Tests;

/// <summary>
/// "Now" for tests that compare times with what they read back, from the clock the application uses (<see cref="MicrosecondClock"/>):
/// PostgreSQL keeps microseconds, but on Linux the system clock gives 100 ns ticks, so a time from <c>DateTimeOffset.UtcNow</c> written
/// and read back can lose its last digit. Every time a test works out from this one (minutes, seconds) stays exact in the database.
/// </summary>
internal static class TestTime
{
    public static DateTimeOffset Now => MicrosecondClock.Instance.GetUtcNow();
}
