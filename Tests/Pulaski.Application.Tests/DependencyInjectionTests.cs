using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Testing;

namespace Pulaski.Application.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void Every_handler_is_registered_and_resolves()
    {
        var handlerTypes = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) ||
                 i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)))
            .ToList();

        var services = new ServiceCollection().AddApplication();
        services.AddSingleton<IRequestContext, FixedRequestContext>();
        services.AddSingleton<Pulaski.Application.Integrations.IDeliveryTargets, Tests.Integrations.DeliveryTests.Targets>();
        new InMemoryStores().Register(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        Assert.NotEmpty(handlerTypes);
        Assert.All(handlerTypes, t => Assert.NotNull(scope.ServiceProvider.GetRequiredService(t)));
    }
}
