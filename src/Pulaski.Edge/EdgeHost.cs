using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Pulaski.Connectors;
using Pulaski.Connectors.EthernetIp;
using Pulaski.Connectors.OpcUa;
using Pulaski.Connectors.Sparkplug;

namespace Pulaski.Edge;

/// <summary>
/// Builds Pulaski Edge's host (D-050): the same worker as a console program, a container, a Windows service or a systemd unit
/// (#25). Under a service manager it logs there, stops when told to and leaves restarts to the manager's recovery settings.
/// </summary>
public static class EdgeHost
{
    public const string ServiceName = "PulaskiEdge";

    /// <summary>Long enough for the connectors to close and the forwarder to finish its batch; Windows waits about 20 s.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    public static HostApplicationBuilder CreateBuilder(string[] args, string? contentRoot = null)
    {
        // A Windows service starts in System32: read appsettings.json and keep the buffer next to the program instead.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = contentRoot ?? (WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null),
        });

        // Each does nothing unless the program runs under that manager.
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.AddSystemd();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);

        builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.Section));
        builder.Services.PostConfigure<EdgeOptions>(o =>
        {
            o.BufferPath = ResolvePath(builder.Environment.ContentRootPath, o.BufferPath);
            o.PkiPath = ResolvePath(builder.Environment.ContentRootPath, o.PkiPath);
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => new EdgeBuffer(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.BufferPath));
        builder.Services.AddSingleton(sp => new TagBinder(
            sp.GetRequiredService<EdgeBuffer>(), sp.GetRequiredService<IOptions<EdgeOptions>>().Value.SpeedInterval,
            sp.GetRequiredService<IOptions<EdgeOptions>>().Value.ProcessValueHeartbeat));
        builder.Services.AddSingleton<IConnectorFactory, SparkplugConnectorFactory>();
        builder.Services.AddSingleton<IConnectorFactory, EthernetIpConnectorFactory>();
        builder.Services.AddSingleton<IConnectorFactory>(sp => new OpcUaConnectorFactory(
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<EdgeOptions>>().Value.PkiPath));
        builder.Services.AddSingleton(sp => new PulaskiClient(
            PulaskiClient.CreateHttp(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.ApiUrl),
            sp.GetRequiredService<IOptions<EdgeOptions>>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PulaskiClient>>()));
        builder.Services.AddHostedService<ConnectorHost>();
        builder.Services.AddHostedService<Forwarder>();
        return builder;
    }

    /// <summary>Whether a worker ended by failing rather than by being stopped.</summary>
    public static bool Failed(IServiceProvider services) =>
        services.GetServices<IHostedService>().OfType<BackgroundService>().Any(s => s.ExecuteTask?.IsFaulted == true);

    /// <summary>A relative path from the configuration, against the program's content root; an absolute one as it is.</summary>
    public static string ResolvePath(string contentRoot, string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(contentRoot, path));
}
