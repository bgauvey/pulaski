using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Inventory;
using Pulaski.Application.Processes;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.Scheduling;
using Pulaski.Application.Units;
using Pulaski.Application.WorkOrders;
using Pulaski.Infrastructure.Boms;
using Pulaski.Infrastructure.EquipmentModel;
using Pulaski.Infrastructure.Events;
using Pulaski.Infrastructure.Inventory;
using Pulaski.Infrastructure.Persistence;
using Pulaski.Infrastructure.Processes;
using Pulaski.Infrastructure.ReferenceData;
using Pulaski.Infrastructure.Scheduling;
using Pulaski.Infrastructure.Units;
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
        services.AddSingleton<ISchedulingReadStore, SchedulingQueries>();
        services.AddSingleton<IProcessReadStore, ProcessQueries>();
        services.AddSingleton<IBomReadStore, BomQueries>();
        services.AddSingleton<IUnitReadStore, UnitQueries>();
        services.AddSingleton<IInventoryReadStore, InventoryQueries>();

        services.AddScoped<DbSession>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DbSession>());
        services.AddScoped<IEventLog, EventLog>();
        services.AddScoped<IEquipmentRepository, EquipmentRepository>();
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IMaterialRepository, MaterialRepository>();
        services.AddScoped<ISchedulingRepository, SchedulingRepository>();
        services.AddScoped<IProcessRepository, ProcessRepository>();
        services.AddScoped<IBomRepository, BomRepository>();
        services.AddScoped<IUnitRepository, UnitRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();

        return services;
    }
}
