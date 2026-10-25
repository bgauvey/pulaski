using Microsoft.Extensions.Logging.Abstractions;
using Pulaski.Connectors;
using Pulaski.Connectors.OpcUa;

namespace Pulaski.Edge.Tests;

/// <summary>
/// The OPC UA connector against a real server (#22). Runs only when PULASKI_OPCUA_TEST_ENDPOINT names one, such as the simulator in
/// deploy/simulator/compose.yaml (opc.tcp://localhost:50000); otherwise each test passes without doing anything.
/// </summary>
public sealed class OpcUaServerTests : IDisposable
{
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("PULASKI_OPCUA_TEST_ENDPOINT");

    private readonly string _pki = Directory.CreateTempSubdirectory("pulaski-opcua-pki-").FullName;

    public void Dispose() => Directory.Delete(_pki, recursive: true);

    private sealed class Sink : ISampleSink
    {
        private readonly Lock _lock = new();
        private readonly List<ConnectorSample> _samples = [];
        private readonly List<DiscoveredAddress> _discovered = [];

        public ValueTask PublishAsync(ConnectorSample sample, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _samples.Add(sample);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DiscoveredAsync(DiscoveredAddress address, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _discovered.Add(address);
            }

            return ValueTask.CompletedTask;
        }

        public (List<ConnectorSample> Samples, List<DiscoveredAddress> Discovered) Snapshot()
        {
            lock (_lock)
            {
                return ([.. _samples], [.. _discovered]);
            }
        }
    }

    // Runs a connector until it has two good readings of the step-up counter, or the time runs out.
    private async Task<Sink> RunAsync(Dictionary<string, string> settings, TimeSpan forAtMost)
    {
        settings["endpoint"] = Endpoint!;
        settings["browseRoot"] = "ns=3;s=OpcPlc";
        var connector = new OpcUaConnectorFactory(TimeProvider.System, _pki)
            .Create(new ConnectorSettings("UA1", settings, new Dictionary<string, string>()), NullLoggerFactory.Instance);
        connector.Watch(new HashSet<string> { "ns=3;s=StepUp" });
        var sink = new Sink();
        using var stop = new CancellationTokenSource(forAtMost);
        var run = connector.RunAsync(sink, stop.Token);
        while (!stop.IsCancellationRequested && sink.Snapshot().Samples.Count(s => s.Quality == SampleQuality.Good) < 2)
        {
            await Task.Delay(200);
        }

        await stop.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }

        await connector.DisposeAsync();
        return sink;
    }

    [Fact]
    public async Task Without_security_it_browses_and_streams_a_changing_value()
    {
        if (Endpoint is null)
        {
            return;
        }

        var (samples, discovered) = (await RunAsync(new() { ["securityMode"] = "none" }, TimeSpan.FromSeconds(30))).Snapshot();

        var good = samples.Where(s => s.Quality == SampleQuality.Good).ToList();
        Assert.True(good.Count >= 2, $"Expected readings of StepUp, got {samples.Count} samples.");
        Assert.All(good, s => Assert.Equal("ns=3;s=StepUp", s.Address));
        Assert.True(good[^1].Timestamp > good[0].Timestamp);
        Assert.Contains(discovered, d => d.Address == "ns=3;s=StepUp");
    }

    [Fact]
    public async Task Signed_and_encrypted_it_refuses_an_unknown_server_and_connects_to_one_trusted_by_thumbprint_or_told_to()
    {
        if (Endpoint is null)
        {
            return;
        }

        var refused = (await RunAsync(new(), TimeSpan.FromSeconds(8))).Snapshot().Samples;
        Assert.DoesNotContain(refused, s => s.Quality == SampleQuality.Good);
        Assert.Contains(refused, s => s.Quality == SampleQuality.Bad);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_pki, "own"), "*", SearchOption.AllDirectories));

        // The refused certificate waits in rejected; trusting it by its thumbprint lets the edge connect securely.
        var rejected = Directory.EnumerateFiles(Path.Combine(_pki, "rejected"), "*.der", SearchOption.AllDirectories).First();
        using var server = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(rejected);
        var byThumbprint = (await RunAsync(new() { ["serverThumbprint"] = server.Thumbprint }, TimeSpan.FromSeconds(30))).Snapshot().Samples;
        Assert.True(byThumbprint.Count(s => s.Quality == SampleQuality.Good) >= 2, $"Expected readings trusting {server.Thumbprint}, got {byThumbprint.Count} samples.");
        Assert.True(File.Exists(Path.Combine(_pki, "trusted", "certs", server.Thumbprint + ".der")));

        var trusted = (await RunAsync(new() { ["trustAnyServer"] = "true" }, TimeSpan.FromSeconds(30))).Snapshot().Samples;
        Assert.True(trusted.Count(s => s.Quality == SampleQuality.Good) >= 2, $"Expected secure readings, got {trusted.Count} samples.");
    }
}
