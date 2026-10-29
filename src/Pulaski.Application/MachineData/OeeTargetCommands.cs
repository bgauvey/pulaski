using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>Sets the site's (no equipment) or a piece of equipment's OEE targets from a production day on (D-073).</summary>
public sealed record SetOeeTargetCommand(Guid SiteId, Guid? EquipmentId, DateOnly EffectiveFrom, OeeTargetValues Values, long ExpectedVersion)
    : ICommand<IReadOnlyList<OeeTargetDto>>;

/// <summary>Ends the site's or a piece of equipment's latest targets on a day (D-073).</summary>
public sealed record EndOeeTargetCommand(Guid SiteId, Guid? EquipmentId, DateOnly EndOn, long ExpectedVersion) : ICommand<IReadOnlyList<OeeTargetDto>>;

/// <summary>Every period of the site's OEE targets, the site's first, then by equipment, newest first.</summary>
public sealed record ListOeeTargetsQuery(Guid SiteId) : IQuery<IReadOnlyList<OeeTargetDto>>;

/// <summary>Write side for OEE targets (D-073).</summary>
public interface IOeeTargetRepository
{
    /// <summary>
    /// Locks the scope: the site's row, or the equipment's (which must be equipment of the site that tracks state); false when it isn't there.
    /// Concurrent changes to one scope serialize on it.
    /// </summary>
    Task<bool> LockScopeAsync(Guid siteId, Guid? equipmentId, CancellationToken cancellationToken);

    /// <summary>The scope's periods, oldest first.</summary>
    Task<IReadOnlyList<Versioned<OeeTarget>>> ListScopeAsync(Guid siteId, Guid? equipmentId, CancellationToken cancellationToken);

    Task InsertAsync(OeeTarget target, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpdateAsync(OeeTarget target, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side for OEE targets.</summary>
public interface IOeeTargetReadStore
{
    Task<IReadOnlyList<OeeTargetDto>> ListAsync(Guid siteId, CancellationToken cancellationToken);
}

internal sealed class OeeTargetWrites(IUnitOfWork unitOfWork, IOeeTargetRepository targets, IOeeTargetReadStore reads, IEventLog eventLog, IEventFactory events, TimeProvider time)
{
    public async Task<IReadOnlyList<OeeTargetDto>> ChangeAsync(
        Guid siteId, Guid? equipmentId, long expectedVersion, string eventType, Func<IReadOnlyList<OeeTarget>, OeeTargetChange> change, CancellationToken cancellationToken)
    {
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken))
        {
            if (!await targets.LockScopeAsync(siteId, equipmentId, cancellationToken))
            {
                throw new NotFoundException(equipmentId is { } id
                    ? $"Equipment {id} isn't equipment of this site that tracks state."
                    : $"Site {siteId} not found.");
            }

            var scope = await targets.ListScopeAsync(siteId, equipmentId, cancellationToken);
            var latest = scope.MaxBy(t => t.Value.EffectiveFrom);
            if ((latest?.Version ?? 0) != expectedVersion)
            {
                throw new ConflictException("These targets were changed by someone else. Reload and try again.");
            }

            var (changed, added) = change([.. scope.Select(t => t.Value)]);
            var at = time.GetUtcNow();
            if (changed is not null)
            {
                await targets.UpdateAsync(changed, at, cancellationToken);
            }

            if (added is not null)
            {
                await targets.InsertAsync(added, at, cancellationToken);
            }

            var subject = equipmentId ?? siteId;
            await eventLog.AppendAsync(events.Create(eventType, "equipment", subject, new { SiteId = siteId, EquipmentId = equipmentId, Changed = changed, Added = added }),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await reads.ListAsync(siteId, cancellationToken);
    }
}

internal sealed class SetOeeTargetHandler(OeeTargetWrites writes, IIdGenerator ids) : ICommandHandler<SetOeeTargetCommand, IReadOnlyList<OeeTargetDto>>
{
    public Task<IReadOnlyList<OeeTargetDto>> HandleAsync(SetOeeTargetCommand command, CancellationToken cancellationToken)
    {
        new Validator().Require(command.Values.Problem() is null, "Targets", command.Values.Problem() ?? "").ThrowIfInvalid();
        var id = ids.NewId();
        return writes.ChangeAsync(command.SiteId, command.EquipmentId, command.ExpectedVersion, EventTypes.OeeTargetSet,
            scope => OeeTargets.Set(scope, id, command.SiteId, command.EquipmentId, command.EffectiveFrom, command.Values), cancellationToken);
    }
}

internal sealed class EndOeeTargetHandler(OeeTargetWrites writes) : ICommandHandler<EndOeeTargetCommand, IReadOnlyList<OeeTargetDto>>
{
    public Task<IReadOnlyList<OeeTargetDto>> HandleAsync(EndOeeTargetCommand command, CancellationToken cancellationToken) =>
        writes.ChangeAsync(command.SiteId, command.EquipmentId, command.ExpectedVersion, EventTypes.OeeTargetEnded,
            scope => new OeeTargetChange(OeeTargets.End(scope, command.EndOn), null), cancellationToken);
}

internal sealed class ListOeeTargetsHandler(IOeeTargetReadStore reads) : IQueryHandler<ListOeeTargetsQuery, IReadOnlyList<OeeTargetDto>>
{
    public Task<IReadOnlyList<OeeTargetDto>> HandleAsync(ListOeeTargetsQuery query, CancellationToken cancellationToken) => reads.ListAsync(query.SiteId, cancellationToken);
}
