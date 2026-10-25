using Pulaski.Edge;

// Pulaski Edge (D-050): runs connectors on plant hardware, buffers what they read in SQLite and forwards it to Pulaski.
using var host = EdgeHost.CreateBuilder(args).Build();
try
{
    await host.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Environment.ExitCode = 1;
    throw;
}

// A worker that failed stops the host; exiting non-zero lets a service manager's recovery restart it (#25).
if (EdgeHost.Failed(host.Services))
{
    Environment.ExitCode = 1;
}
