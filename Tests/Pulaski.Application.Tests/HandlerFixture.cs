using Microsoft.Extensions.Time.Testing;
using Pulaski.Application.Abstractions;
using Pulaski.Testing;

namespace Pulaski.Application.Tests;

/// <summary>In-memory stores plus a fixed clock and actor, shared by the handler tests.</summary>
internal sealed class HandlerFixture
{
    public static readonly DateTimeOffset Now = new(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);

    public HandlerFixture()
    {
        var time = new FakeTimeProvider(Now);
        Ids = new UuidV7Generator(time);
        Events = new EventFactory(Ids, time, new FixedRequestContext());
    }

    public InMemoryStores Stores { get; } = new();

    public IIdGenerator Ids { get; }

    public IEventFactory Events { get; }
}
