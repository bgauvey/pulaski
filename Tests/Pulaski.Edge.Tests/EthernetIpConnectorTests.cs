using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulaski.Connectors;
using Pulaski.Connectors.EthernetIp;

namespace Pulaski.Edge.Tests;

/// <summary>The EtherNet/IP connector (#24), with a scripted PLC in place of libplctag.</summary>
public class EthernetIpConnectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private sealed class ScriptedPlc : ICipTagReader
    {
        public Dictionary<string, Func<object>> Values { get; } = [];

        public List<string> Forgotten { get; } = [];

        public List<string> Read { get; } = [];

        public Task<object> ReadAsync(EthernetIpAddress address, CancellationToken cancellationToken)
        {
            Read.Add(address.TagName + "|" + address.Type);
            return Task.FromResult(Values[address.Address]());
        }

        public ValueTask ForgetAsync(string address)
        {
            Forgotten.Add(address);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Sink : ISampleSink
    {
        public List<ConnectorSample> Samples { get; } = [];

        public List<DiscoveredAddress> Discovered { get; } = [];

        public ValueTask PublishAsync(ConnectorSample sample, CancellationToken cancellationToken)
        {
            Samples.Add(sample);
            return ValueTask.CompletedTask;
        }

        public ValueTask DiscoveredAsync(DiscoveredAddress address, CancellationToken cancellationToken)
        {
            Discovered.Add(address);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void An_address_is_a_tag_name_and_its_type_dint_when_left_out()
    {
        Assert.Equal(("Program:Main.State", "dint", 4), (EthernetIpAddress.Parse("Program:Main.State").TagName, EthernetIpAddress.Parse("Program:Main.State").Type, EthernetIpAddress.Parse("Program:Main.State").ElementSize));
        Assert.Equal(("Melt.Temp", "real", "REAL"), (EthernetIpAddress.Parse("Melt.Temp|REAL").TagName, EthernetIpAddress.Parse("Melt.Temp|REAL").Type, EthernetIpAddress.Parse("Melt.Temp|real").DataType));
        Assert.Equal(1, EthernetIpAddress.Parse("Status.5|bool").ElementSize);
        Assert.Throws<FormatException>(() => EthernetIpAddress.Parse("Melt.Temp|float"));
    }

    [Fact]
    public async Task Tags_are_read_each_poll_a_failing_one_goes_bad_once_and_comes_back()
    {
        var time = new FakeTimeProvider(T0);
        var plc = new ScriptedPlc();
        var state = 6;
        var broken = false;
        plc.Values["Line1.State"] = () => state;
        plc.Values["Melt.Temp|real"] = () => broken ? throw new IOException("Connection reset by peer") : 210.5f;
        var connector = new EthernetIpConnector(
            new EthernetIpOptions("PLC1", "10.0.0.20", "1,0", "controllogix", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), plc, time, NullLogger<EthernetIpConnector>.Instance);
        connector.Watch(new HashSet<string> { "Melt.Temp|real", "Line1.State", "1Bad|nope" });
        var sink = new Sink();

        await connector.PollAsync(sink, CancellationToken.None);
        broken = true;
        state = 2;
        time.Advance(TimeSpan.FromSeconds(1));
        await connector.PollAsync(sink, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await connector.PollAsync(sink, CancellationToken.None);   // still broken: not published again
        broken = false;
        time.Advance(TimeSpan.FromSeconds(1));
        await connector.PollAsync(sink, CancellationToken.None);

        Assert.Equal(
        [
            ("1Bad|nope", null, SampleQuality.Bad, T0),
            ("Line1.State", 6, SampleQuality.Good, T0), ("Melt.Temp|real", 210.5f, SampleQuality.Good, T0),
            ("Line1.State", 2, SampleQuality.Good, T0.AddSeconds(1)), ("Melt.Temp|real", null, SampleQuality.Bad, T0.AddSeconds(1)),
            ("Line1.State", 2, SampleQuality.Good, T0.AddSeconds(2)),
            ("Line1.State", 2, SampleQuality.Good, T0.AddSeconds(3)), ("Melt.Temp|real", 210.5f, SampleQuality.Good, T0.AddSeconds(3)),
        ],
        sink.Samples.Select(s => (s.Address, s.Value, s.Quality, s.Timestamp)));

        // Announced once each, with its type; a failed read drops the handle so the next one reconnects.
        Assert.Equal([("Line1.State", "DINT"), ("Melt.Temp|real", "REAL")], sink.Discovered.Select(d => (d.Address, d.DataType)));
        Assert.Equal(["Melt.Temp|real", "Melt.Temp|real"], plc.Forgotten);
        Assert.Contains("Melt.Temp|real", plc.Read);
    }

    [Fact]
    public void Settings_default_to_a_controllogix_read_every_second()
    {
        var options = EthernetIpOptions.From(new ConnectorSettings("PLC1", new Dictionary<string, string> { ["gateway"] = "10.0.0.20", ["path"] = "1,0" }, new Dictionary<string, string>()));
        Assert.Equal(("controllogix", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), (options.Plc, options.PollInterval, options.Timeout));
        Assert.Throws<InvalidOperationException>(() => EthernetIpOptions.From(new ConnectorSettings("PLC1", new Dictionary<string, string>(), new Dictionary<string, string>())));
    }

    [Fact]
    public async Task The_native_library_loads_and_an_unreachable_plc_fails_the_read()
    {
        // Nothing listens on port 1: libplctag (its native library, embedded for this platform) refuses the read rather than hanging.
        await using var reader = new LibplctagReader(new EthernetIpOptions("PLC1", "127.0.0.1:1", "1,0", "controllogix", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync(EthernetIpAddress.Parse("Line1.State"), CancellationToken.None));
        Assert.IsNotType<DllNotFoundException>(failure);
        Assert.IsNotType<TypeInitializationException>(failure);
    }
}
