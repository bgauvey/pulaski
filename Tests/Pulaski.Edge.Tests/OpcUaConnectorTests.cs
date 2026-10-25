using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulaski.Connectors;
using Pulaski.Connectors.OpcUa;

namespace Pulaski.Edge.Tests;

/// <summary>The OPC UA connector (#22), with scripted sessions in place of a server.</summary>
public class OpcUaConnectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private static OpcUaOptions Options() => OpcUaOptions.From(
        new ConnectorSettings("UA1", new Dictionary<string, string> { ["endpoint"] = "opc.tcp://plc1:4840" }, new Dictionary<string, string>()), "/tmp/pki");

    private sealed class ScriptedSession(bool fails) : IOpcUaSession
    {
        public event Action<OpcUaValue>? ValueChanged;

        public event Action<string>? ConnectionLost;

        public List<IReadOnlySet<string>> Monitored { get; } = [];

        public bool Disposed { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken) => fails ? throw new IOException("BadNotConnected") : Task.CompletedTask;

        public Task<IReadOnlyList<OpcUaNode>> BrowseAsync(string? root, int depth, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpcUaNode>>([new("ns=2;s=Line1.State", null, null), new("ns=2;s=Melt.Temp", null, null)]);

        public Task MonitorAsync(IReadOnlySet<string> nodeIds, CancellationToken cancellationToken)
        {
            Monitored.Add(nodeIds);
            return Task.CompletedTask;
        }

        public void Change(OpcUaValue value) => ValueChanged?.Invoke(value);

        public void Lose() => ConnectionLost?.Invoke("the server stopped answering");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Sink : ISampleSink
    {
        private readonly Lock _lock = new();

        public List<ConnectorSample> Samples { get; } = [];

        public List<DiscoveredAddress> Discovered { get; } = [];

        public ValueTask PublishAsync(ConnectorSample sample, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Samples.Add(sample);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DiscoveredAsync(DiscoveredAddress address, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Discovered.Add(address);
            }

            return ValueTask.CompletedTask;
        }

        public int Count(Func<ConnectorSample, bool> match)
        {
            lock (_lock)
            {
                return Samples.Count(match);
            }
        }
    }

    private static async Task Until(Func<bool> condition, FakeTimeProvider? clock = null)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            // A reconnect waits on the clock: nudge it along, however late the wait started.
            clock?.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public void A_data_change_is_a_sample_at_its_source_time_and_a_status_that_isnt_good_is_bad()
    {
        var time = new FakeTimeProvider(T0);
        var connector = new OpcUaConnector(Options(), () => new ScriptedSession(false), time, NullLogger<OpcUaConnector>.Instance);

        Assert.Equal(("ns=2;s=A", (object?)6, T0.AddSeconds(-2), SampleQuality.Good),
            Fields(connector.Sample(new OpcUaValue("ns=2;s=A", 6, true, T0.AddSeconds(-2), T0.AddSeconds(-1)))));
        Assert.Equal(("ns=2;s=A", (object?)null, T0.AddSeconds(-1), SampleQuality.Bad),
            Fields(connector.Sample(new OpcUaValue("ns=2;s=A", 6, false, null, T0.AddSeconds(-1)))));
        Assert.Equal(T0, connector.Sample(new OpcUaValue("ns=2;s=A", 1.5, true, null, null)).Timestamp);

        static (string, object?, DateTimeOffset, SampleQuality) Fields(ConnectorSample s) => (s.Address, s.Value, s.Timestamp, s.Quality);
    }

    [Fact]
    public async Task It_reconnects_after_a_failure_announces_what_it_browsed_follows_the_bound_nodes_and_marks_them_bad_when_lost()
    {
        var time = new FakeTimeProvider(T0);
        var sessions = new List<ScriptedSession>();
        var connector = new OpcUaConnector(Options(), () =>
        {
            var session = new ScriptedSession(fails: sessions.Count == 0);   // the first attempt fails
            sessions.Add(session);
            return session;
        }, time, NullLogger<OpcUaConnector>.Instance);
        connector.Watch(new HashSet<string> { "ns=2;s=Line1.State" });
        var sink = new Sink();
        using var stop = new CancellationTokenSource();
        var run = connector.RunAsync(sink, stop.Token);

        // The failed attempt marks the bound node bad; a second later it tries again and connects.
        await Until(() => sink.Count(s => s.Quality == SampleQuality.Bad) == 1);
        await Until(() => sessions.Count == 2 && sessions[1].Monitored.Count == 1, time);
        Assert.True(sessions[0].Disposed);
        Assert.Equal(["ns=2;s=Line1.State", "ns=2;s=Melt.Temp"], sink.Discovered.Select(d => d.Address));
        Assert.Equal(["ns=2;s=Line1.State"], sessions[1].Monitored[0]);

        sessions[1].Change(new OpcUaValue("ns=2;s=Line1.State", 6, true, T0.AddSeconds(1), null));
        await Until(() => sink.Count(s => s.Value is 6) == 1);

        connector.Watch(new HashSet<string> { "ns=2;s=Line1.State", "ns=2;s=Melt.Temp" });
        await Until(() => sessions[1].Monitored.Count == 2);
        Assert.Equal(2, sessions[1].Monitored[1].Count);

        // Lost: both bound nodes go bad, and it reconnects after the (reset) one-second wait.
        sessions[1].Lose();
        await Until(() => sink.Count(s => s.Quality == SampleQuality.Bad) == 3);
        await Until(() => sessions.Count == 3 && sessions[2].Monitored.Count == 1, time);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
