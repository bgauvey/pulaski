using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Execution;
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

namespace Pulaski.Testing;

/// <summary>
/// In-memory stand-ins for every database port, for handler and endpoint tests. They keep data
/// and versions but not transactional isolation; the Infrastructure tests cover the real thing.
/// </summary>
public sealed class InMemoryStores
{
    public InMemoryStores()
    {
        WorkOrders.OpenJobs = Scheduling.HasOpenJobs;
        Processes.Eligibility = Scheduling.Eligibility;
        Execution.WorkOrderOf = id => Scheduling.FindJob(id)?.Value.WorkOrderId;
    }

    public InMemoryUnitOfWork UnitOfWork { get; } = new();

    public InMemoryEventLog Events { get; } = new();

    public InMemoryEquipment Equipment { get; } = new();

    public InMemoryWorkOrders WorkOrders { get; } = new();

    public InMemoryMaterials Materials { get; } = new();

    public InMemoryScheduling Scheduling { get; } = new();

    public InMemoryProcesses Processes { get; } = new();

    public InMemoryBoms Boms { get; } = new();

    public InMemoryUnits Units { get; } = new();

    private InMemoryLotRecords? _lotRecords;
    private InMemoryContainers? _containers;

    public InMemoryInventory Inventory { get; } = new();

    public InMemoryExpectedReceipts ExpectedReceipts { get; } = new();

    public InMemoryContainers Containers => _containers ??= new InMemoryContainers(Inventory);

    public InMemoryLotRecords LotRecords => _lotRecords ??= new InMemoryLotRecords(Inventory);

    public InMemoryMaterialAdmin MaterialAdmin { get; } = new();

    public InMemoryCapabilities Capabilities { get; } = new();

    public InMemoryExecution Execution { get; } = new();

    public InMemorySecurity Security { get; } = new();

    public InMemoryShifts Shifts { get; } = new();

    public InMemoryCalendarExceptions CalendarExceptions { get; } = new();

    public InMemoryProductionReports ProductionReports { get; } = new();

    private InMemoryRunHistory? _runHistory;

    public InMemoryRunHistory RunHistory => _runHistory ??= new(Scheduling);

    public InMemoryLabor Labor { get; } = new();

    public InMemoryMachineData MachineData { get; } = new();

    public InMemoryEdge Edge { get; } = new();

    public InMemoryStops Stops { get; } = new();

    public InMemoryOee Oee { get; } = new();

    public InMemoryTerminals Terminals { get; } = new();

    /// <summary>Replaces any registered stores with these instances.</summary>
    public IServiceCollection Register(IServiceCollection services)
    {
        services.RemoveAll<IUnitOfWork>();
        services.RemoveAll<IEventLog>();
        services.RemoveAll<IEquipmentRepository>();
        services.RemoveAll<IEquipmentReadStore>();
        services.RemoveAll<IWorkOrderRepository>();
        services.RemoveAll<IWorkOrderReadStore>();
        services.RemoveAll<IMaterialRepository>();
        services.RemoveAll<IReferenceDataReadStore>();
        services.RemoveAll<ISchedulingReadStore>();
        services.RemoveAll<ISchedulingRepository>();
        services.RemoveAll<IProcessReadStore>();
        services.RemoveAll<IProcessRepository>();
        services.RemoveAll<IBomReadStore>();
        services.RemoveAll<IBomRepository>();
        services.RemoveAll<IUnitReadStore>();
        services.RemoveAll<IUnitRepository>();
        services.RemoveAll<IInventoryReadStore>();
        services.RemoveAll<IInventoryRepository>();
        services.RemoveAll<IContainerRepository>();
        services.RemoveAll<IContainerReadStore>();
        services.RemoveAll<IExpectedReceiptRepository>();
        services.RemoveAll<IExpectedReceiptReadStore>();
        services.RemoveAll<ILotRecordsReadStore>();
        services.RemoveAll<ILotRecordsRepository>();
        services.RemoveAll<IMaterialReadStore>();
        services.RemoveAll<IMaterialAdminRepository>();
        services.RemoveAll<ICapabilityRepository>();
        services.RemoveAll<IExecutionRepository>();
        services.RemoveAll<IExecutionReadStore>();
        services.RemoveAll<ISecurityReadStore>();
        services.RemoveAll<ISecurityRepository>();
        services.RemoveAll<ISiteLookup>();
        services.RemoveAll<IPasswordHasher>();
        services.RemoveAll<IShiftReadStore>();
        services.RemoveAll<IShiftRepository>();
        services.RemoveAll<ICalendarExceptionReadStore>();
        services.RemoveAll<ICalendarExceptionRepository>();
        services.RemoveAll<IProductionReportReadStore>();
        services.RemoveAll<IRunHistoryReadStore>();
        services.RemoveAll<ILaborReadStore>();
        services.RemoveAll<ILaborRepository>();
        services.RemoveAll<IMachineDataReadStore>();
        services.RemoveAll<IMachineDataRepository>();
        services.RemoveAll<IEdgeReadStore>();
        services.RemoveAll<IEdgeRepository>();
        services.RemoveAll<IStopReadStore>();
        services.RemoveAll<IStopRepository>();
        services.RemoveAll<IOeeReadStore>();
        services.RemoveAll<IQualificationRepository>();
        services.RemoveAll<ITerminalRepository>();
        services.RemoveAll<ITerminalReadStore>();
        services.RemoveAll<IBadgeHasher>();
        services.RemoveAll<IQualificationReadStore>();

        services.AddSingleton<IUnitOfWork>(UnitOfWork);
        services.AddSingleton<IEventLog>(Events);
        services.AddSingleton<IEquipmentRepository>(Equipment);
        services.AddSingleton<IEquipmentReadStore>(Equipment);
        services.AddSingleton<IWorkOrderRepository>(WorkOrders);
        services.AddSingleton<IWorkOrderReadStore>(WorkOrders);
        services.AddSingleton<IMaterialRepository>(Materials);
        services.AddSingleton<IReferenceDataReadStore>(Materials);
        services.AddSingleton<ISchedulingReadStore>(Scheduling);
        services.AddSingleton<ISchedulingRepository>(Scheduling);
        services.AddSingleton<IProcessReadStore>(Processes);
        services.AddSingleton<IProcessRepository>(Processes);
        services.AddSingleton<IBomReadStore>(Boms);
        services.AddSingleton<IBomRepository>(Boms);
        services.AddSingleton<IUnitReadStore>(Units);
        services.AddSingleton<IUnitRepository>(Units);
        services.AddSingleton<IInventoryReadStore>(Inventory);
        services.AddSingleton<IInventoryRepository>(Inventory);
        services.AddSingleton<IContainerRepository>(Containers);
        services.AddSingleton<IContainerReadStore>(Containers);
        services.AddSingleton<IExpectedReceiptRepository>(ExpectedReceipts);
        services.AddSingleton<IExpectedReceiptReadStore>(ExpectedReceipts);
        services.AddSingleton<ILotRecordsReadStore>(LotRecords);
        services.AddSingleton<ILotRecordsRepository>(LotRecords);
        services.AddSingleton<IMaterialReadStore>(MaterialAdmin);
        services.AddSingleton<IMaterialAdminRepository>(MaterialAdmin);
        services.AddSingleton<ICapabilityRepository>(Capabilities);
        services.AddSingleton<IExecutionRepository>(Execution);
        services.AddSingleton<IExecutionReadStore>(Execution);
        services.AddSingleton<ISecurityReadStore>(Security);
        services.AddSingleton<ISecurityRepository>(Security);
        services.AddSingleton<ISiteLookup>(Security);
        services.AddSingleton<IPasswordHasher>(new PlainPasswordHasher());
        services.AddSingleton<IShiftReadStore>(Shifts);
        services.AddSingleton<IShiftRepository>(Shifts);
        services.AddSingleton<ICalendarExceptionReadStore>(CalendarExceptions);
        services.AddSingleton<ICalendarExceptionRepository>(CalendarExceptions);
        services.AddSingleton<IProductionReportReadStore>(ProductionReports);
        services.AddSingleton<IRunHistoryReadStore>(RunHistory);
        services.AddSingleton<ILaborReadStore>(Labor);
        services.AddSingleton<ILaborRepository>(Labor);
        services.AddSingleton<IMachineDataReadStore>(MachineData);
        services.AddSingleton<IMachineDataRepository>(MachineData);
        services.AddSingleton<IEdgeReadStore>(Edge);
        services.AddSingleton<IEdgeRepository>(Edge);
        services.AddSingleton<IStopReadStore>(Stops);
        services.AddSingleton<IStopRepository>(Stops);
        services.AddSingleton<IOeeReadStore>(Oee);
        services.AddSingleton<IQualificationRepository>(Labor);
        services.AddSingleton<IQualificationReadStore>(Labor);
        services.AddSingleton<ITerminalRepository>(Terminals);
        services.AddSingleton<ITerminalReadStore>(Terminals);
        services.AddSingleton<IBadgeHasher>(new ReversingBadgeHasher());
        return services;
    }
}

public sealed class FixedRequestContext(string actorId = "test-user", Guid? userId = null) : IRequestContext
{
    public string ActorId => actorId;

    public Guid? UserId => userId;

    public Guid CorrelationId { get; } = Guid.Parse("01900000-0000-7000-8000-000000000001");
}

public sealed class InMemoryUnitOfWork : IUnitOfWork
{
    private int _begun;
    private int _committed;

    public int Begun => _begun;

    public int Committed => _committed;

    public Task<ITransaction> BeginAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _begun);
        return Task.FromResult<ITransaction>(new Transaction(this));
    }

    private sealed class Transaction(InMemoryUnitOfWork owner) : ITransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner._committed);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class InMemoryEventLog : IEventLog
{
    private readonly List<EventEnvelope> _events = [];

    public IReadOnlyList<EventEnvelope> Appended
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public Task AppendAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        lock (_events)
        {
            _events.Add(envelope);
        }

        return Task.CompletedTask;
    }
}
