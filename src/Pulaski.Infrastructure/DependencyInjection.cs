using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Execution;
using Pulaski.Application.Integrations;
using Pulaski.Application.Inventory;
using Pulaski.Application.Labor;
using Pulaski.Application.MachineData;
using Pulaski.Application.Materials;
using Pulaski.Application.Processes;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.Scheduling;
using Pulaski.Application.Security;
using Pulaski.Application.Shifts;
using Pulaski.Application.Units;
using Pulaski.Application.WorkOrders;
using Pulaski.Infrastructure.Boms;
using Pulaski.Infrastructure.EquipmentModel;
using Pulaski.Infrastructure.Events;
using Pulaski.Infrastructure.Execution;
using Pulaski.Infrastructure.Integrations;
using Pulaski.Infrastructure.Inventory;
using Pulaski.Infrastructure.Labor;
using Pulaski.Infrastructure.MachineData;
using Pulaski.Infrastructure.Materials;
using Pulaski.Infrastructure.Persistence;
using Pulaski.Infrastructure.Processes;
using Pulaski.Infrastructure.ReferenceData;
using Pulaski.Infrastructure.Scheduling;
using Pulaski.Infrastructure.Security;
using Pulaski.Infrastructure.Shifts;
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
        services.AddSingleton<IExecutionReadStore, ExecutionQueries>();
        services.AddSingleton<SecurityQueries>();
        services.AddSingleton<ISecurityReadStore>(sp => sp.GetRequiredService<SecurityQueries>());
        services.AddSingleton<ISiteLookup>(sp => sp.GetRequiredService<SecurityQueries>());
        services.AddSingleton<IInventoryReadStore, InventoryQueries>();
        services.AddSingleton<ILotRecordsReadStore, LotRecordsQueries>();
        services.AddSingleton<IExpectedReceiptReadStore, ExpectedReceiptQueries>();
        services.AddSingleton<IContainerReadStore, ContainerQueries>();
        services.AddSingleton<DatabaseMaintenance>();
        services.AddSingleton<IMaterialReadStore, MaterialQueries>();
        services.AddSingleton<IShiftReadStore, ShiftQueries>();
        services.AddSingleton<ICalendarExceptionReadStore, CalendarExceptionQueries>();
        services.AddSingleton<IProductionReportReadStore, ProductionReportQueries>();
        services.AddSingleton<IRunHistoryReadStore, RunHistoryQueries>();
        services.AddSingleton<IQualificationReadStore, QualificationQueries>();
        services.AddSingleton<ITerminalReadStore, TerminalQueries>();
        services.AddSingleton<ILaborReadStore, LaborQueries>();
        services.AddSingleton<IMachineDataReadStore, MachineDataQueries>();
        services.AddSingleton<IEdgeReadStore, EdgeQueries>();
        services.AddSingleton<IStopReadStore, StopQueries>();
        services.AddSingleton<IOeeReadStore, OeeQueries>();

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
        services.AddScoped<IAllocationRepository, AllocationRepository>();
        services.AddScoped<Application.Materials.IMaterialConnectionStore, Materials.MaterialConnectionStore>();
        services.AddScoped<Application.Materials.ILineBatchSizes, Materials.LineBatchSizes>();
        services.AddScoped<IAllocationReadStore, AllocationQueries>();
        services.AddScoped<ILotRecordsRepository, LotRecordsRepository>();
        services.AddScoped<IExpectedReceiptRepository, ExpectedReceiptRepository>();
        services.AddScoped<IContainerRepository, ContainerRepository>();
        services.AddScoped<IDeliveryStore, DeliveryStore>();
        services.AddSingleton<DeliveryRelay>();
        services.AddScoped<IMaterialAdminRepository, MaterialAdminRepository>();
        services.AddScoped<ICapabilityRepository, CapabilityRepository>();
        services.AddScoped<IExecutionRepository, ExecutionRepository>();
        services.AddScoped<ISecurityRepository, SecurityRepository>();
        services.AddScoped<IShiftRepository, ShiftRepository>();
        services.AddScoped<ICalendarExceptionRepository, CalendarExceptionRepository>();
        services.AddScoped<IQualificationRepository, QualificationRepository>();
        services.AddScoped<ITerminalRepository, TerminalRepository>();
        services.AddScoped<ILaborRepository, LaborRepository>();
        services.AddScoped<IMachineDataRepository, MachineDataRepository>();
        services.AddScoped<IEdgeRepository, EdgeRepository>();
        services.AddScoped<IStopRepository, StopRepository>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        return services;
    }
}
