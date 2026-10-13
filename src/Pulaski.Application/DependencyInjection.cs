using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulaski.Application.Abstractions;

namespace Pulaski.Application;

public static class DependencyInjection
{
    /// <summary>Registers every command and query handler in this assembly, plus id and event services.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        services.AddScoped<IEventFactory, EventFactory>();

        var handlerInterfaces = new[] { typeof(ICommandHandler<,>), typeof(IQueryHandler<,>) };
        var handlers = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false });

        foreach (var type in handlers)
        {
            foreach (var contract in type.GetInterfaces())
            {
                if (contract.IsGenericType && handlerInterfaces.Contains(contract.GetGenericTypeDefinition()))
                {
                    services.AddScoped(contract, type);
                }
            }
        }

        return services;
    }
}
