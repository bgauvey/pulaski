using Microsoft.Extensions.Time.Testing;
using Pulaski.Connectors;
using Pulaski.Connectors.Sparkplug;

namespace Pulaski.Edge.Tests;

/// <summary>The Sparkplug B payload codec and the host application's handling of births, data and deaths (D-050).</summary>
public class SparkplugTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
    private static ulong Ms(int seconds) => (ulong)T0.AddSeconds(seconds).ToUnixTimeMilliseconds();

    private static byte[] Payload(ulong seq, params SparkplugMetric[] metrics) => new SparkplugPayload(Ms(0), seq, metrics).Encode();

    [Fact]
    public void A_payload_survives_encoding_with_every_value_type()
    {
        var payload = new SparkplugPayload(Ms(1), 7,
        [
            SparkplugMetric.Of("State", 6, alias: 1),
            SparkplugMetric.Of("Negative", -5),
            SparkplugMetric.Of("Count", 1234567890123L),
            SparkplugMetric.Of("Speed", 61.5f),
            SparkplugMetric.Of("Precise", 0.125d),
            SparkplugMetric.Of("Running", true),
            SparkplugMetric.Of("Name", "Packer A"),
            SparkplugMetric.Of("Nothing", null),
        ]);

        var decoded = SparkplugPayload.Decode(payload.Encode());

        Assert.Equal((Ms(1), (ulong?)7), (decoded.Timestamp!.Value, decoded.Sequence));
        Assert.Equal<object?>([6, -5, 1234567890123L, 61.5f, 0.125d, true, "Packer A", null], decoded.Metrics.Select(m => m.Value));
        Assert.Equal((ulong?)1, decoded.Metrics[0].Alias);
        Assert.True(decoded.Metrics[^1].IsNull);
    }

    [Fact]
    public void A_short_payload_is_refused()
    {
        // Cut inside the metric (no sequence number after it): the metric's length points past the end.
        var bytes = new SparkplugPayload(Ms(0), null, [SparkplugMetric.Of("Status/StateCurrent", 6)]).Encode();
        Assert.Throws<FormatException>(() => SparkplugPayload.Decode(bytes.AsSpan(0, bytes.Length - 2)));
    }

    [Fact]
    public void A_topic_reads_its_group_type_node_and_device()
    {
        Assert.Equal(new SparkplugTopic("Plant1", "DDATA", "Packing", "PACK-01"), SparkplugTopic.Parse("spBv1.0/Plant1/DDATA/Packing/PACK-01"));
        Assert.Equal("Plant1/Packing:Node Control/Rebirth", SparkplugTopic.Parse("spBv1.0/Plant1/NBIRTH/Packing")!.Address("Node Control/Rebirth"));
        Assert.Null(SparkplugTopic.Parse("spBv1.0/STATE/pulaski"));
        Assert.Null(SparkplugTopic.Parse("other/topic"));
    }

    [Fact]
    public void A_birth_names_metrics_data_refers_to_them_by_alias_and_a_death_loses_them()
    {
        var host = new SparkplugHost(new FakeTimeProvider(T0));
        host.Handle("spBv1.0/Plant1/NBIRTH/Packing", Payload(0, SparkplugMetric.Of("bdSeq", 0L)));
        var birth = host.Handle("spBv1.0/Plant1/DBIRTH/Packing/PACK-01",
            Payload(1, SparkplugMetric.Of("Status/StateCurrent", 4, alias: 10), SparkplugMetric.Of("Admin/ProdProcessedCount", 0L, alias: 11)));

        Assert.Equal(["Plant1/Packing/PACK-01:Status/StateCurrent", "Plant1/Packing/PACK-01:Admin/ProdProcessedCount"], birth.Discovered.Select(d => d.Address));
        Assert.Equal<object?>([4, 0L], birth.Samples.Select(s => s.Value));

        var data = host.Handle("spBv1.0/Plant1/DDATA/Packing/PACK-01",
            new SparkplugPayload(Ms(5), 2, [new SparkplugMetric(null, 10, Ms(5), SparkplugDataType.Int32, false, 6)]).Encode());
        var sample = Assert.Single(data.Samples);
        Assert.Equal(("Plant1/Packing/PACK-01:Status/StateCurrent", (object?)6, T0.AddSeconds(5)), (sample.Address, sample.Value, sample.Timestamp));
        Assert.Null(data.Rebirth);

        var death = host.Handle("spBv1.0/Plant1/NDEATH/Packing", Payload(0, SparkplugMetric.Of("bdSeq", 0L)));
        // The node's own bdSeq and both device metrics.
        Assert.Equal(3, death.Samples.Count);
        Assert.All(death.Samples, s => Assert.Equal(SampleQuality.Bad, s.Quality));
    }

    [Fact]
    public void Data_from_a_node_whose_birth_was_missed_or_whose_sequence_skips_asks_for_a_rebirth_at_most_every_ten_seconds()
    {
        var time = new FakeTimeProvider(T0);
        var host = new SparkplugHost(time);

        var unknown = host.Handle("spBv1.0/Plant1/DDATA/Packing/PACK-01", Payload(5, SparkplugMetric.Of("State", 6)));
        Assert.Equal(("Plant1", "Packing"), unknown.Rebirth);
        Assert.Null(host.Handle("spBv1.0/Plant1/DDATA/Packing/PACK-01", Payload(6, SparkplugMetric.Of("State", 6))).Rebirth);

        time.Advance(TimeSpan.FromSeconds(11));
        host.Handle("spBv1.0/Plant1/NBIRTH/Packing", Payload(0));
        host.Handle("spBv1.0/Plant1/DBIRTH/Packing/PACK-01", Payload(1, SparkplugMetric.Of("State", 4, alias: 1)));
        var skipped = host.Handle("spBv1.0/Plant1/DDATA/Packing/PACK-01", Payload(3, SparkplugMetric.Of("State", 6)));
        Assert.Equal(("Plant1", "Packing"), skipped.Rebirth);
        Assert.Empty(skipped.Samples);

        var command = SparkplugPayload.Decode(host.RebirthCommand());
        Assert.Equal(("Node Control/Rebirth", (object?)true), (command.Metrics[0].Name, command.Metrics[0].Value));
    }

    [Theory]
    [InlineData("Plant1/Packing/PACK-01:Status/StateCurrent", "Plant1", "Packing")]
    [InlineData("Plant1/Packing:Node Control/Scan Rate", "Plant1", "Packing")]
    public void An_address_names_the_node_to_ask_for_a_rebirth(string address, string group, string node) =>
        Assert.Equal((group, node), SparkplugConnector.NodeOf(address));

    [Fact]
    public void Losing_the_broker_loses_every_address_heard()
    {
        var host = new SparkplugHost(new FakeTimeProvider(T0));
        host.Handle("spBv1.0/Plant1/NBIRTH/Packing", Payload(0, SparkplugMetric.Of("Line speed", 1.5d)));

        var lost = host.LoseAll();

        Assert.Equal(("Plant1/Packing:Line speed", SampleQuality.Bad), (Assert.Single(lost).Address, lost[0].Quality));
        Assert.Empty(host.LoseAll());
    }
}
