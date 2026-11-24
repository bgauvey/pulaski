using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Pulaski.Edge.Tests;

/// <summary>The edge's host (#25): the same worker however it is run, with its files beside the program.</summary>
public sealed class EdgeHostTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pulaski-edge-host-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_relative_buffer_path_is_kept_beside_the_program_and_an_absolute_one_as_given()
    {
        using var host = EdgeHost.CreateBuilder([], _root).Build();

        Assert.Equal(Path.Combine(_root, "pulaski-edge.db"), host.Services.GetRequiredService<IOptions<EdgeOptions>>().Value.BufferPath);
        Assert.Equal(EdgeHost.ShutdownTimeout, host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);

        var elsewhere = Path.Combine(_root, "data", "buffer.db");
        Assert.Equal(elsewhere, EdgeHost.ResolvePath("/opt/pulaski-edge", elsewhere));
        Assert.Equal(Path.GetFullPath("/opt/pulaski-edge/buffer.db"), EdgeHost.ResolvePath("/opt/pulaski-edge", "buffer.db"));
    }

    [Fact]
    public void Every_worker_resolves_with_the_service_integrations_in_place()
    {
        using var host = EdgeHost.CreateBuilder(["--Edge:BufferPath=buffer.db"], _root).Build();

        var workers = host.Services.GetServices<IHostedService>().ToList();
        Assert.Contains(workers, w => w is ConnectorHost);
        Assert.Contains(workers, w => w is Forwarder);
        Assert.True(File.Exists(Path.Combine(_root, "buffer.db")));
        Assert.False(EdgeHost.Failed(EdgeHost.Workers(host.Services)));
    }

    [Fact]
    public async Task A_worker_that_fails_marks_the_run_as_failed_so_the_service_manager_restarts_it()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _root });
        builder.Services.AddHostedService<Failing>();
        using var host = builder.Build();

        await host.StartAsync();
        await Task.Delay(100);
        await host.StopAsync();

        Assert.True(EdgeHost.Failed(EdgeHost.Workers(host.Services)));
    }

    [Fact]
    public async Task Whether_it_failed_can_be_told_after_the_run_has_disposed_the_host()
    {
        // As Program does: RunAsync disposes the host, so asking its services afterwards threw ObjectDisposedException on every stop.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _root });
        builder.Services.AddHostedService<Failing>();
        using var host = builder.Build();
        var workers = EdgeHost.Workers(host.Services);

        try
        {
            await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (InvalidOperationException)
        {
            // The failing worker's own exception, when the host rethrows it.
        }

        Assert.True(EdgeHost.Failed(workers));
    }

    private sealed class Failing : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("Broken on purpose.");
        }
    }
}
