using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Quality;
using Pulaski.Domain;
using Pulaski.Domain.Quality;

namespace Pulaski.Application.Quality;

/// <summary>What an NCR's source gives it (#133): its site, and the lots and runs it touches.</summary>
public sealed record NcrSource(Guid SiteId, IReadOnlyList<Guid> LotIds, IReadOnlyList<Guid> RunIds);

/// <summary>Nonconformance reports' writes (#133, D-124), in the current unit of work.</summary>
public interface INcrRepository
{
    Task<long> NextNumberAsync(CancellationToken cancellationToken);

    /// <summary>The source's site, lots and runs; null when there is no such source.</summary>
    Task<NcrSource?> SourceAsync(string kind, Guid id, DateTimeOffset? at, CancellationToken cancellationToken);

    /// <summary>Those of <paramref name="lotIds"/> at <paramref name="siteId"/>.</summary>
    Task<IReadOnlyList<Guid>> LotsAtAsync(Guid siteId, IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    Task<bool> IsSiteUserAsync(Guid userId, Guid siteId, CancellationToken cancellationToken);

    Task InsertAsync(Nonconformance ncr, CancellationToken cancellationToken);

    /// <summary>The NCR, locked: every change to it and its actions takes this first.</summary>
    Task<Versioned<Nonconformance>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> UpdateAsync(Nonconformance ncr, DateTimeOffset at, CancellationToken cancellationToken);

    Task LinkAsync(Guid ncrId, IReadOnlyCollection<Guid> lotIds, IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<NcrAction>> ListActionsAsync(Guid ncrId, CancellationToken cancellationToken);

    Task InsertActionAsync(NcrAction action, string by, CancellationToken cancellationToken);

    Task UpdateActionAsync(NcrAction action, CancellationToken cancellationToken);
}

/// <summary>Nonconformance reports' reads (#133).</summary>
public interface INcrReadStore
{
    Task<IReadOnlyList<NcrSummaryDto>> ListAsync(Guid? siteId, string? status, Guid? lotId, DateOnly today, CancellationToken cancellationToken);

    Task<NcrDto?> GetAsync(Guid id, DateOnly today, CancellationToken cancellationToken);

    Task<IReadOnlyList<NcrAssigneeDto>> AssigneesAsync(Guid siteId, CancellationToken cancellationToken);
}

/// <summary>
/// Raises an NCR (#133): on its own or from a complaint at <paramref name="SiteId"/>; else from a held or rejected lot, a result, an SPC alert or a
/// checklist answer, whose site it takes, with the lots and runs it touches. <paramref name="RaiserSites"/>: where the caller may raise NCRs
/// (null: everywhere).
/// </summary>
public sealed record RaiseNcrCommand(
    Guid? SiteId, string? Title, string? Description, string? Severity, string? SourceKind, Guid? SourceId, DateTimeOffset? SourceAt, Guid? OwnerUserId,
    IReadOnlyList<Guid> LotIds, IReadOnlyList<Guid>? RaiserSites) : ICommand<NcrCreatedResponse>;

public sealed record UpdateNcrCommand(Guid Id, long ExpectedVersion, string? Title, string? Description, string? Severity, Guid? OwnerUserId, IReadOnlyList<Guid> AddLotIds)
    : ICommand<VersionResponse>;

public sealed record InvestigateNcrCommand(Guid Id, long ExpectedVersion, string? RootCause, IReadOnlyList<string> Whys, bool Complete) : ICommand<VersionResponse>;

public sealed record AddNcrActionCommand(Guid Id, string? Kind, string? Description, Guid? OwnerUserId, DateOnly? DueOn) : ICommand<CreatedResponse>;

/// <summary>Marks an action done (or cancels it) with a note: by its owner, or someone who manages NCRs at the site (<paramref name="MayManage"/>).</summary>
public sealed record FinishNcrActionCommand(Guid Id, Guid ActionId, string? Note, bool Cancel, bool MayManage) : ICommand<VersionResponse>;

public sealed record CloseNcrCommand(Guid Id, long ExpectedVersion, string? Disposition, string? Note) : ICommand<VersionResponse>;

public sealed record ListNcrsQuery(Guid? SiteId, string? Status, Guid? LotId) : IQuery<IReadOnlyList<NcrSummaryDto>>;

public sealed record GetNcrQuery(Guid Id) : IQuery<NcrDto>;

public sealed record ListNcrAssigneesQuery(Guid SiteId) : IQuery<IReadOnlyList<NcrAssigneeDto>>;

internal sealed class RaiseNcrHandler(
    IUnitOfWork unitOfWork, INcrRepository ncrs, IEventLog eventLog, IEventFactory events, IIdGenerator ids) : ICommandHandler<RaiseNcrCommand, NcrCreatedResponse>
{
    public async Task<NcrCreatedResponse> HandleAsync(RaiseNcrCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var kind = command.SourceKind ?? NcrSources.Manual;
        NcrSource source;
        if (kind is NcrSources.Manual or NcrSources.Complaint)
        {
            source = new NcrSource(command.SiteId ?? throw ValidationException.Field("SiteId", $"Choose the site."), [], []);
        }
        else
        {
            source = command.SourceId is { } sourceId && await ncrs.SourceAsync(kind, sourceId, command.SourceAt, cancellationToken) is { } found
                ? found
                : throw new NotFoundException($"That {UserMessage.Code(kind)} isn't one Pulaski knows.");
        }

        if (command.RaiserSites is { } sites && !sites.Contains(source.SiteId))
        {
            throw new ForbiddenException($"You may not raise NCRs at that site.");
        }

        if (command.OwnerUserId is { } owner && !await ncrs.IsSiteUserAsync(owner, source.SiteId, cancellationToken))
        {
            throw new DomainRuleException($"The owner needs a role at the NCR's site.");
        }

        var number = $"NCR-{await ncrs.NextNumberAsync(cancellationToken):000000}";
        var envelope = events.Create(EventTypes.NcrRaised, "nonconformance", ids.NewId(), new { Number = number, kind, command.SourceId, command.Severity, command.Title });
        var ncr = Nonconformance.Raise(
            envelope.Subject, source.SiteId, number, command.Title, command.Description, command.Severity, kind, command.SourceId, command.SourceAt, command.OwnerUserId,
            envelope.ActorId, envelope.OccurredAt);
        var lots = source.LotIds.Union(await ncrs.LotsAtAsync(source.SiteId, command.LotIds, cancellationToken)).ToList();
        await ncrs.InsertAsync(ncr, cancellationToken);
        await ncrs.LinkAsync(ncr.Id, lots, source.RunIds, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NcrCreatedResponse(ncr.Id, ncr.Number);
    }
}

internal sealed class UpdateNcrHandler(IUnitOfWork unitOfWork, INcrRepository ncrs, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<UpdateNcrCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateNcrCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await NcrLocks.LockAsync(ncrs, command.Id, command.ExpectedVersion, cancellationToken);
        if (command.OwnerUserId is { } owner && owner != current.OwnerUserId && !await ncrs.IsSiteUserAsync(owner, current.SiteId, cancellationToken))
        {
            throw new DomainRuleException($"The owner needs a role at the NCR's site.");
        }

        var after = current.Edit(command.Title, command.Description, command.Severity, command.OwnerUserId);
        var lots = await ncrs.LotsAtAsync(current.SiteId, command.AddLotIds, cancellationToken);
        var envelope = events.Create(EventTypes.NcrChanged, "nonconformance", current.Id, new { Before = current, After = after, AddedLots = lots });
        var version = await ncrs.UpdateAsync(after, time.GetUtcNow(), cancellationToken);
        await ncrs.LinkAsync(current.Id, lots, [], cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class InvestigateNcrHandler(IUnitOfWork unitOfWork, INcrRepository ncrs, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<InvestigateNcrCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(InvestigateNcrCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await NcrLocks.LockAsync(ncrs, command.Id, command.ExpectedVersion, cancellationToken);
        var after = current.Investigate(command.RootCause, command.Whys, command.Complete);
        var envelope = events.Create(EventTypes.NcrInvestigated, "nonconformance", current.Id, new { after.RootCause, after.Whys, after.Status });
        var version = await ncrs.UpdateAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class AddNcrActionHandler(IUnitOfWork unitOfWork, INcrRepository ncrs, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<AddNcrActionCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(AddNcrActionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var ncr = await NcrLocks.LockAsync(ncrs, command.Id, null, cancellationToken);
        ncr.EnsureOpen();
        if (command.OwnerUserId is { } owner && !await ncrs.IsSiteUserAsync(owner, ncr.SiteId, cancellationToken))
        {
            throw new DomainRuleException($"The owner needs a role at the NCR's site.");
        }

        var action = NcrAction.Add(ids.NewId(), ncr.Id, command.Kind, command.Description, command.OwnerUserId, command.DueOn);
        var envelope = events.Create(EventTypes.NcrActionAdded, "nonconformance", ncr.Id, new { Action = action });
        await ncrs.InsertActionAsync(action, envelope.ActorId, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(action.Id, 0);
    }
}

internal sealed class FinishNcrActionHandler(IUnitOfWork unitOfWork, INcrRepository ncrs, IEventLog eventLog, IEventFactory events, IRequestContext request)
    : ICommandHandler<FinishNcrActionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(FinishNcrActionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var ncr = await NcrLocks.LockAsync(ncrs, command.Id, null, cancellationToken);
        ncr.EnsureOpen();
        var action = (await ncrs.ListActionsAsync(ncr.Id, cancellationToken)).FirstOrDefault(a => a.Id == command.ActionId)
            ?? throw new NotFoundException($"That isn't one of {ncr.Number}'s actions.");
        if (!command.MayManage && (action.OwnerUserId is null || action.OwnerUserId != request.UserId))
        {
            throw new ForbiddenException($"Only its owner, or someone who manages NCRs, finishes an action.");
        }

        var envelope = events.Create(command.Cancel ? EventTypes.NcrActionCancelled : EventTypes.NcrActionDone, "nonconformance", ncr.Id, new { command.ActionId, command.Note });
        var after = command.Cancel ? action.Cancel(command.Note, envelope.ActorId, envelope.OccurredAt) : action.Complete(command.Note, envelope.ActorId, envelope.OccurredAt);
        await ncrs.UpdateActionAsync(after, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

internal sealed class CloseNcrHandler(IUnitOfWork unitOfWork, INcrRepository ncrs, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<CloseNcrCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(CloseNcrCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await NcrLocks.LockAsync(ncrs, command.Id, command.ExpectedVersion, cancellationToken);
        var envelope = events.Create(EventTypes.NcrClosed, "nonconformance", current.Id, new { command.Disposition, command.Note });
        var closed = current.Close(command.Disposition, command.Note, await ncrs.ListActionsAsync(current.Id, cancellationToken), envelope.ActorId, envelope.OccurredAt);
        var version = await ncrs.UpdateAsync(closed, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class ListNcrsHandler(INcrReadStore store, TimeProvider time) : IQueryHandler<ListNcrsQuery, IReadOnlyList<NcrSummaryDto>>
{
    public Task<IReadOnlyList<NcrSummaryDto>> HandleAsync(ListNcrsQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.SiteId, query.Status, query.LotId, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), cancellationToken);
}

internal sealed class GetNcrHandler(INcrReadStore store, TimeProvider time) : IQueryHandler<GetNcrQuery, NcrDto>
{
    public async Task<NcrDto> HandleAsync(GetNcrQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), cancellationToken)
        ?? throw new NotFoundException($"Nonconformance report {query.Id} not found.");
}

internal sealed class ListNcrAssigneesHandler(INcrReadStore store) : IQueryHandler<ListNcrAssigneesQuery, IReadOnlyList<NcrAssigneeDto>>
{
    public Task<IReadOnlyList<NcrAssigneeDto>> HandleAsync(ListNcrAssigneesQuery query, CancellationToken cancellationToken) =>
        store.AssigneesAsync(query.SiteId, cancellationToken);
}

internal static class NcrLocks
{
    public static async Task<Nonconformance> LockAsync(INcrRepository ncrs, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var current = await ncrs.GetForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Nonconformance report {id} not found.");
        return expectedVersion is { } expected && current.Version != expected
            ? throw new ConflictException($"{current.Value.Number} was changed by someone else. Reload and try again.")
            : current.Value;
    }
}
