using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.WorkOrders;
using Pulaski.Infrastructure.EquipmentModel;
using Pulaski.Infrastructure.Events;
using Pulaski.Infrastructure.Persistence;
using Pulaski.Infrastructure.ReferenceData;
using Pulaski.Infrastructure.WorkOrders;

namespace Pulaski.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the PostgreSQL data source, the read-side query classes (singletons, one
    /// connection per query) and the write side (scoped: one session and transaction per command).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        DapperSetup.Configure();

        services.AddNpgsqlDataSource(connectionString);

        services.AddSingleton<IEquipmentReadStore, EquipmentQueries>();
        services.AddSingleton<IWorkOrderReadStore, WorkOrderQueries>();
        services.AddSingleton<IReferenceDataReadStore, ReferenceDataQueries>();

        services.AddScoped<DbSession>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DbSession>());
        services.AddScoped<IEventLog, EventLog>();
        services.AddScoped<IEquipmentRepository, EquipmentRepository>();
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IMaterialRepository, MaterialRepository>();

        return services;
    }
}
