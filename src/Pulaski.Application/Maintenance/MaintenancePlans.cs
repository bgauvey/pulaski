using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Maintenance;
using Pulaski.Domain;
using Pulaski.Domain.Maintenance;

namespace Pulaski.Application.Maintenance;

/// <summary>A plan's last request, for working out where its meter counts from.</summary>
public sealed record PlanLastRequest(Guid Id, string Status, DateTimeOffset? ClosedAt);

/// <summary>Write side for preventive plans (#127d, D-115), in the current unit of work.</summary>
public interface IMaintenancePlanRepository
{
    Task<Versioned<MaintenancePlan>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertAsync(MaintenancePlan plan, DateTimeOffset at, string by, CancellationToken cancellationToken);

    Task<long> UpdateAsync(MaintenancePlan plan, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The plan's latest request, if any.</summary>
    Task<PlanLastRequest?> GetLastRequestAsync(Guid planId, CancellationToken cancellationToken);
}

/// <summary>Read side for preventive plans (#127d).</summary>
public interface IMaintenancePlanReadStore
{
    /// <summary>The site's plans, each with its equipment and latest request; the meter reading is left for the handler.</summary>
    Task<IReadOnlyList<MaintenancePlanDto>> ListAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The active plans' ids, for the runner to take one at a time.</summary>
    Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken);

    /// <summary>The meter since <paramref name="from"/> to <paramref name="until"/>: run hours (Execute time) or machine counts (processed).</summary>
    Task<decimal> ReadMeterAsync(Guid equipmentId, string meter, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);
}

/// <summary>A preventive plan for a line or machine (#127d): what is done, every so many run hours or counts, at a priority.</summary>
public sealed record CreateMaintenancePlanCommand(Guid EquipmentId, string? Name, string? Meter, decimal Interval, string? Priority) : ICommand<CreatedResponse>;

/// <summary>A plan's name, interval, priority and whether it is on; its meter never changes.</summary>
public sealed record UpdateMaintenancePlanCommand(Guid Id, long ExpectedVersion, string? Name, decimal Interval, string? Priority, bool IsActive)
    : ICommand<VersionResponse>;

internal sealed class CreateMaintenancePlanHandler(
    IUnitOfWork unitOfWork, IMaintenanceRepository requests, IMaintenancePlanRepository plans, IEventLog eventLog, IEventFactory events,
    IIdGenerator ids, TimeProvider time) : ICommandHandler<CreateMaintenancePlanCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateMaintenancePlanCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var equipment = await requests.LockEquipmentAsync(command.EquipmentId, cancellationToken)
            ?? throw new NotFoundException($"Equipment {command.EquipmentId} not found.");
        if (!MaintenanceEquipmentRules.Levels.Contains(equipment.Level) || equipment.SiteId is not { } siteId)
        {
            throw new DomainRuleException($"{equipment.Code} isn't a line or machine; plan maintenance for one.");
        }

        var now = time.GetUtcNow();
        var plan = MaintenancePlan.Create(ids.NewId(), siteId, equipment.Id, command.Name, command.Meter, command.Interval, command.Priority, now);
        var envelope = events.Create(EventTypes.MaintenancePlanCreated, "maintenance_plan", plan.Id, new { Equipment = equipment.Code, After = plan });
        var version = await plans.InsertAsync(plan, now, envelope.ActorId, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(plan.Id, version);
    }
}

internal sealed class UpdateMaintenancePlanHandler(
    IUnitOfWork unitOfWork, IMaintenancePlanRepository plans, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<UpdateMaintenancePlanCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateMaintenancePlanCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await plans.GetForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Maintenance plan {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Name} was changed by someone else. Reload and try again.");
        }

        var now = time.GetUtcNow();
        var after = current.Value.Edit(command.Name, command.Interval, command.Priority, command.IsActive, now);
        if (after == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.MaintenancePlanChanged, "maintenance_plan", after.Id, new { Before = current.Value, After = after });
        var version = await plans.UpdateAsync(after, now, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

/// <summary>The site's preventive plans (#127d), each with its meter so far and the request it raised last.</summary>
public sealed record ListMaintenancePlansQuery(Guid SiteId) : IQuery<IReadOnlyList<MaintenancePlanDto>>;

internal sealed class ListMaintenancePlansHandler(IMaintenancePlanReadStore store, TimeProvider time)
    : IQueryHandler<ListMaintenancePlansQuery, IReadOnlyList<MaintenancePlanDto>>
{
    public async Task<IReadOnlyList<MaintenancePlanDto>> HandleAsync(ListMaintenancePlansQuery query, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var listed = await store.ListAsync(query.SiteId, cancellationToken);
        var figured = new List<MaintenancePlanDto>(listed.Count);
        foreach (var p in listed)
        {
            // While its request is open the meter waits; otherwise it counts from its start or its last request's close.
            var waiting = p.LastRequestStatus is { } status && status != MaintenanceStatuses.Closed;
            var from = MaintenancePlanRunner.Domain(p).CountsFrom(p.LastRequestClosedAt);
            figured.Add(p with
            {
                CountsFrom = from,
                Reading = waiting || !p.IsActive ? null : await store.ReadMeterAsync(p.EquipmentId, p.Meter, from, now, cancellationToken),
            });
        }

        return figured;
    }
}

/// <summary>
/// Raises the requests preventive plans are due (#127d): each active plan in its own transaction, the plan locked first, then its equipment as
/// raising does. A plan whose request is still open is skipped; the meter counts from its start or its last request's close. Runs as <c>system</c>.
/// </summary>
public sealed class MaintenancePlanRunner(
    IUnitOfWork unitOfWork, IMaintenanceRepository requests, IMaintenancePlanRepository plans, IMaintenancePlanReadStore meters, IEventLog eventLog,
    IEventFactory events, IIdGenerator ids, TimeProvider time)
{
    /// <returns>How many requests it raised.</returns>
    public async Task<int> RaiseDueAsync(CancellationToken cancellationToken)
    {
        var raised = 0;
        foreach (var planId in await meters.ListActiveIdsAsync(cancellationToken))
        {
            await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
            var plan = (await plans.GetForUpdateAsync(planId, cancellationToken))?.Value;
            if (plan is null || !plan.IsActive)
            {
                continue;
            }

            var last = await plans.GetLastRequestAsync(plan.Id, cancellationToken);
            if (last is not null && last.Status != MaintenanceStatuses.Closed)
            {
                continue;
            }

            var now = time.GetUtcNow();
            var from = plan.CountsFrom(last?.ClosedAt);
            var reading = await meters.ReadMeterAsync(plan.EquipmentId, plan.Meter, from, now, cancellationToken);
            if (!plan.IsDue(reading))
            {
                continue;
            }

            var equipment = await requests.LockEquipmentAsync(plan.EquipmentId, cancellationToken);
            if (equipment is not { IsActive: true })
            {
                continue;
            }

            var number = MaintenanceRequest.NumberFor(await requests.NextNumberAsync(cancellationToken));
            var envelope = events.Create(
                EventTypes.MaintenanceRequestRaised, "maintenance_request", ids.NewId(),
                new { number, Equipment = equipment.Code, plan.Priority, Description = plan.Name, Plan = new { plan.Id, plan.Meter, plan.Interval, Reading = reading, From = from } });
            var request = MaintenanceRequest.Raise(
                envelope.Subject, plan.SiteId, plan.EquipmentId, number, plan.Priority, plan.Name, envelope.OccurredAt, envelope.ActorId, planId: plan.Id);
            await requests.InsertAsync(request, now, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            raised++;
        }

        return raised;
    }

    internal static MaintenancePlan Domain(MaintenancePlanDto p) =>
        new(p.Id, p.SiteId, p.EquipmentId, p.Name, p.Meter, p.Interval, p.Priority, p.IsActive, p.StartedAt);
}

/// <summary>What a request or plan may be for (#127): equipment below an area.</summary>
public static class MaintenanceEquipmentRules
{
    public static IReadOnlyList<string> Levels { get; } = ["work_center", "work_unit", "equipment_module"];
}
