using Microsoft.Extensions.Time.Testing;
using Pulaski.Connectors.Sparkplug;
using Pulaski.ProcessSimulator;

namespace Pulaski.Tools.Tests;

/// <summary>The demo's process-value simulator (D-101).</summary>
public class ProcessSimulatorTests
{
    private static readonly ProcessShift Shift = new("Process/MeltTemperature", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), 1.5);

    [Fact]
    public void The_shift_comes_at_the_end_of_every_period_after_a_stable_start()
    {
        Assert.False(Shift.IsActive(TimeSpan.Zero));
        Assert.False(Shift.IsActive(TimeSpan.FromMinutes(19.9)));
        Assert.True(Shift.IsActive(TimeSpan.FromMinutes(20)));
        Assert.True(Shift.IsActive(TimeSpan.FromMinutes(29.9)));
        Assert.False(Shift.IsActive(TimeSpan.FromMinutes(30)));
        Assert.True(Shift.IsActive(TimeSpan.FromMinutes(55)));
        Assert.False((Shift with { Every = TimeSpan.Zero }).IsActive(TimeSpan.FromMinutes(25)));
    }

    [Fact]
    public void Readings_scatter_around_the_target_and_move_by_the_shift_while_it_lasts()
    {
        var model = new ProcessModel(ProcessModel.Extruder, Shift, new Random(42));
        var melt = ProcessModel.Extruder[0];
        var stable = Enumerable.Range(0, 4000).Select(_ => model.Read(TimeSpan.FromMinutes(5))[0].Value).ToList();
        var shifted = Enumerable.Range(0, 4000).Select(_ => model.Read(TimeSpan.FromMinutes(25))[0].Value).ToList();

        Assert.Equal(melt.Target, stable.Average(), 1);
        Assert.Equal(melt.Sigma, StandardDeviation(stable), 1);
        Assert.Equal(melt.Target + (1.5 * melt.Sigma), shifted.Average(), 1);
        Assert.Equal(42.00, model.Read(TimeSpan.FromMinutes(25))[1].Value, 0);
        Assert.All(stable, v => Assert.Equal(v, Math.Round(v, 1)));
    }

    [Fact]
    public void Pulaski_reads_its_births_and_data_without_asking_for_a_rebirth()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var node = new SparkplugNode("Plant1", "ExtruderProcess", "EXT-01-EXT", time);
        var host = new SparkplugHost(time);
        var values = new[] { ("Process/MeltTemperature", 215.3), ("Process/ProfileWidth", 42.01) };

        node.NewSession(0);
        var births = node.Births(values).Select(m => host.Handle(m.Topic, m.Payload)).ToList();
        var data = Enumerable.Range(0, 300).Select(_ => Send(host, node.Data(values))).ToList();

        Assert.All(births.Concat(data), o => Assert.Null(o.Rebirth));
        Assert.Contains(births[1].Discovered, d => d.Address == "Plant1/ExtruderProcess/EXT-01-EXT:Process/MeltTemperature");
        var last = data[^1].Samples.Single(s => s.Address.EndsWith(":Process/ProfileWidth", StringComparison.Ordinal));
        Assert.Equal(42.01, last.Value);
    }

    [Fact]
    public void A_rebirth_request_is_recognised_and_its_births_start_the_sequence_again()
    {
        var time = new FakeTimeProvider();
        var node = new SparkplugNode("Plant1", "ExtruderProcess", "EXT-01-EXT", time);
        var host = new SparkplugHost(time);
        var values = new[] { ("Process/MeltTemperature", 215.0) };
        node.NewSession(3);
        foreach (var (topic, payload) in node.Births(values))
        {
            host.Handle(topic, payload);
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(Send(host, node.Data(values)).Rebirth);
        }

        Assert.True(SparkplugNode.AsksForRebirth(host.RebirthCommand()));
        Assert.False(SparkplugNode.AsksForRebirth(new byte[] { 0xFF, 0x00 }));
        var reborn = node.Births(values).Select(m => host.Handle(m.Topic, m.Payload)).ToList();
        Assert.All(reborn, o => Assert.Null(o.Rebirth));
        Assert.Null(Send(host, node.Data(values)).Rebirth);
    }

    private static SparkplugOutcome Send(SparkplugHost host, (string Topic, byte[] Payload) message) => host.Handle(message.Topic, message.Payload);

    private static double StandardDeviation(List<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }
}
