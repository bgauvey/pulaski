using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;

namespace Pulaski.Application;

public static class DependencyInjection
{
    /// <summary>Registers every command and query handler in this assembly, plus id and event services and the material source adapters (D-053).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        services.AddScoped<IEventFactory, EventFactory>();
        services.AddSingleton<IMaterialSourceAdapter, CsvMaterialSource>();
        services.AddSingleton<IMaterialSourceAdapter, B2mmlMaterialSource>();

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
