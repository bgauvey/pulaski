using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Pulaski.Testing;

namespace Pulaski.Api.Tests;

/// <summary>Hosts the API with in-memory stores, so endpoint and error mapping run without a database.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public InMemoryStores Stores { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Never opened: every store is replaced below.
        builder.UseSetting("ConnectionStrings:Pulaski", "Host=unused.invalid;Database=none");
        builder.ConfigureTestServices(services => Stores.Register(services));
    }
}
