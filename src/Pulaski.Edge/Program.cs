using Microsoft.Extensions.Options;
using Pulaski.Connectors;
using Pulaski.Connectors.Sparkplug;
using Pulaski.Edge;

// Pulaski Edge (D-050): runs connectors on plant hardware, buffers what they read in SQLite and forwards it to Pulaski.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new EdgeBuffer(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.BufferPath));
builder.Services.AddSingleton(sp => new TagBinder(sp.GetRequiredService<EdgeBuffer>(), sp.GetRequiredService<IOptions<EdgeOptions>>().Value.SpeedInterval));
builder.Services.AddSingleton<IConnectorFactory, SparkplugConnectorFactory>();
builder.Services.AddSingleton(sp => new PulaskiClient(
    PulaskiClient.CreateHttp(sp.GetRequiredService<IOptions<EdgeOptions>>().Value.ApiUrl),
    sp.GetRequiredService<IOptions<EdgeOptions>>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PulaskiClient>>()));
builder.Services.AddHostedService<ConnectorHost>();
builder.Services.AddHostedService<Forwarder>();

builder.Build().Run();
