using Pulaski.Connectors;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Edge.Tests;

/// <summary>The edge's buffer and the binding of samples to tags (D-050).</summary>
public sealed class EdgeTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pulaski-edge-test-{Guid.NewGuid():N}.db");
    private readonly Guid _connection = Guid.CreateVersion7();
    private readonly Guid _state = Guid.CreateVersion7();
    private readonly Guid _speed = Guid.CreateVersion7();

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    private EdgeConfigDto Config => new()
    {
        EdgeNodeId = Guid.CreateVersion7(),
        Code = "GW-1",
        SiteId = Guid.CreateVersion7(),
        IsActive = true,
        Tags =
        [
            new() { Id = _state, EquipmentId = Guid.CreateVersion7(), Role = "state", ConnectionId = _connection, Address = "P/N/D:State" },
            new() { Id = _speed, EquipmentId = Guid.CreateVersion7(), Role = "machine_speed", ConnectionId = _connection, Address = "P/N/D:Speed" },
        ],
    };

    [Fact]
    public void The_buffer_keeps_signals_in_order_across_a_restart_until_they_are_applied()
    {
        Guid stream;
        using (var buffer = new EdgeBuffer(_path))
        {
            stream = buffer.StreamId;
            buffer.Append(_state, T0, 6, good: true);
            buffer.Append(_state, T0.AddSeconds(1), 2.5m, good: true);
            buffer.Append(_state, T0.AddSeconds(2), null, good: false);
            buffer.SaveConfig(Config);
        }

        using var reopened = new EdgeBuffer(_path);
        Assert.Equal(stream, reopened.StreamId);
        var taken = reopened.Take(10);
        Assert.Equal([1L, 2L, 3L], taken.Select(s => s.Sequence));
        Assert.Equal<decimal?>([6m, 2.5m, null], taken.Select(s => s.Value));
        Assert.False(taken[2].Good);
        Assert.Equal(T0.AddSeconds(1), taken[1].OccurredAt);

        reopened.Remove(2);
        Assert.Equal([3L], reopened.Take(10).Select(s => s.Sequence));
        Assert.Equal(4L, reopened.Append(_state, T0, 1, true));
        Assert.Equal("GW-1", reopened.LoadConfig()!.Code);
    }

    [Fact]
    public void Only_bound_addresses_become_signals_and_only_when_they_change_or_are_lost()
    {
        using var buffer = new EdgeBuffer(_path);
        var binder = new TagBinder(buffer, TimeSpan.FromSeconds(5));
        binder.Use(Config);

        Assert.Equal(1, binder.Accept(_connection, new ConnectorSample("P/N/D:State", 6, T0)));
        Assert.Equal(0, binder.Accept(_connection, new ConnectorSample("P/N/D:State", 6L, T0.AddSeconds(1))));
        Assert.Equal(0, binder.Accept(_connection, new ConnectorSample("P/N/D:Unbound", 1, T0)));
        Assert.Equal(0, binder.Accept(Guid.CreateVersion7(), new ConnectorSample("P/N/D:State", 2, T0)));
        Assert.Equal(1, binder.Accept(_connection, new ConnectorSample("P/N/D:State", true, T0.AddSeconds(2))));
        Assert.Equal(1, binder.Accept(_connection, new ConnectorSample("P/N/D:State", null, T0.AddSeconds(3), SampleQuality.Bad)));
        Assert.Equal(0, binder.Accept(_connection, new ConnectorSample("P/N/D:State", null, T0.AddSeconds(4), SampleQuality.Bad)));

        var signals = buffer.Take(10);
        Assert.Equal<decimal?>([6m, 1m, null], signals.Select(s => s.Value));
        Assert.Equal([true, true, false], signals.Select(s => s.Good));
        // Seen per connection: the state on both connections, and the unbound address.
        Assert.Equal(["P/N/D:State", "P/N/D:State", "P/N/D:Unbound"], binder.TakeSeen().Select(s => s.Address).Order());
        Assert.Empty(binder.TakeSeen());
    }

    [Fact]
    public void A_tag_bound_later_starts_with_the_last_value_seen_at_its_address()
    {
        using var buffer = new EdgeBuffer(_path);
        var binder = new TagBinder(buffer, TimeSpan.FromSeconds(5));
        binder.Use(Config with { Tags = [] });
        binder.Accept(_connection, new ConnectorSample("P/N/D:State", 4, T0));

        Assert.Equal(1, binder.Use(Config));
        Assert.Equal(0, binder.Use(Config));
        var signal = Assert.Single(buffer.Take(10));
        Assert.Equal((_state, (decimal?)4m, T0), (signal.TagId, signal.Value, signal.OccurredAt));
    }

    [Fact]
    public void A_speed_is_forwarded_at_most_every_interval_and_its_latest_value_follows()
    {
        using var buffer = new EdgeBuffer(_path);
        var binder = new TagBinder(buffer, TimeSpan.FromSeconds(5));
        binder.Use(Config);

        binder.Accept(_connection, new ConnectorSample("P/N/D:Speed", 60.0, T0));
        binder.Accept(_connection, new ConnectorSample("P/N/D:Speed", 61.0, T0.AddSeconds(1)));
        binder.Accept(_connection, new ConnectorSample("P/N/D:Speed", 0.0, T0.AddSeconds(2)));
        Assert.Equal(0, binder.Flush(T0.AddSeconds(3)));
        Assert.Equal(1, binder.Flush(T0.AddSeconds(6)));

        Assert.Equal<decimal?>([60m, 0m], buffer.Take(10).Select(s => s.Value));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(6, 6)]
    [InlineData(6L, 6)]
    [InlineData(2.5f, 2.5)]
    [InlineData("42", 42)]
    public void Values_become_numbers(object value, double expected) => Assert.Equal((decimal)expected, TagBinder.Number(value));

    [Fact]
    public void Values_that_arent_numbers_become_none() =>
        Assert.Equal<decimal?>([null, null, null], new object?[] { null, "Running", double.NaN }.Select(TagBinder.Number));
}
