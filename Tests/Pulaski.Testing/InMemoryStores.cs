using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Inventory;
using Pulaski.Application.Materials;
using Pulaski.Application.Processes;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.Scheduling;
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

    public InMemoryInventory Inventory { get; } = new();

    public InMemoryMaterialAdmin MaterialAdmin { get; } = new();

    public InMemoryCapabilities Capabilities { get; } = new();

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
        services.RemoveAll<IMaterialReadStore>();
        services.RemoveAll<IMaterialAdminRepository>();
        services.RemoveAll<ICapabilityRepository>();

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
        services.AddSingleton<IMaterialReadStore>(MaterialAdmin);
        services.AddSingleton<IMaterialAdminRepository>(MaterialAdmin);
        services.AddSingleton<ICapabilityRepository>(Capabilities);
        return services;
    }
}

public sealed class FixedRequestContext : IRequestContext
{
    public string ActorId => "test-user";

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
