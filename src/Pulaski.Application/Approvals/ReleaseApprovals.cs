using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Approvals;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Approvals;

namespace Pulaski.Application.Approvals;

/// <summary>A draft as approval needs it (#138): its document and version, a code and number to name it, the sites that decide whether approval applies.</summary>
public sealed record ReleasableDraft(Guid DocumentId, Guid VersionId, string Code, int Number, IReadOnlyList<Guid> SiteIds);

/// <summary>
/// One kind of document that is released (#138): a process, a BOM or a specification. Locks the document then its draft (the order its own
/// commands use), says why it can't be released, and releases it, all in the caller's transaction.
/// </summary>
public interface IReleasableDocument
{
    string Kind { get; }

    /// <summary>The event types that save its drafts: their actors are the draft's editors, who may not approve it.</summary>
    IReadOnlyList<string> DraftSavedEvents { get; }

    /// <summary>Locks the document and its draft; <paramref name="expectedVersion"/> checks the draft's version when given.</summary>
    /// <exception cref="NotFoundException">There is no such draft.</exception>
    Task<ReleasableDraft> LockAsync(Guid documentId, Guid versionId, long? expectedVersion, CancellationToken cancellationToken);

    /// <exception cref="DomainRuleException">It isn't a draft, or it can't be released as it is.</exception>
    Task EnsureReleasableAsync(ReleasableDraft draft, CancellationToken cancellationToken);

    /// <summary>Releases the locked draft with <paramref name="note"/>, as the document's own release does; what it says afterwards.</summary>
    Task<IReadOnlyList<MessageDto>> ReleaseAsync(ReleasableDraft draft, string? note, CancellationToken cancellationToken);
}

/// <summary>Release approval's writes (#138, D-122), in the current unit of work.</summary>
public interface IReleaseApprovalRepository
{
    /// <summary>The site's settings, locked; a site without a row gets one, all off.</summary>
    Task<Versioned<ReleaseApprovalSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken);

    Task<long> UpdateSettingsAsync(ReleaseApprovalSettings settings, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Those of <paramref name="siteIds"/> whose settings ask for approval of <paramref name="kind"/>.</summary>
    Task<IReadOnlyList<Guid>> SitesRequiringAsync(string kind, IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken);

    /// <summary>The version's waiting request, locked.</summary>
    Task<ReleaseRequest?> GetPendingForVersionAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>A request, unlocked: to find its document, which is locked first.</summary>
    Task<ReleaseRequest?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ReleaseRequest?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task InsertAsync(ReleaseRequest request, CancellationToken cancellationToken);

    Task UpdateAsync(ReleaseRequest request, CancellationToken cancellationToken);

    /// <summary>Who saved the version's draft: actors of <paramref name="eventTypes"/> on the document naming the version.</summary>
    Task<IReadOnlyList<string>> EditorsAsync(Guid documentId, Guid versionId, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken);
}

/// <summary>Release approval's reads (#138).</summary>
public interface IReleaseApprovalReadStore
{
    Task<ReleaseApprovalSettingsDto> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The sites whose setting decides a document's approval: a process's own site; for BOMs and specifications, the sites that can make the material.</summary>
    Task<IReadOnlyList<Guid>> SitesForAsync(string kind, Guid documentId, CancellationToken cancellationToken);

    Task<bool> AnyRequiresAsync(string kind, IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken);

    /// <summary>The document's requests, latest first, with names; the editors of each version for <see cref="ReleaseRequestDto.MayDecide"/>.</summary>
    Task<IReadOnlyList<(ReleaseRequest Request, int VersionNumber, string? SubmittedByName, string? DecidedByName)>> ListAsync(
        string kind, Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> EditorsAsync(Guid documentId, Guid versionId, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken);
}

/// <summary>What a document's own commands check while approval may apply (#138).</summary>
public static class ReleaseGate
{
    /// <summary>A draft waiting for approval doesn't change.</summary>
    public static async Task EnsureEditableAsync(IReleaseApprovalRepository? approvals, Guid versionId, CancellationToken cancellationToken)
    {
        if (approvals is not null && await approvals.GetPendingForVersionAsync(versionId, cancellationToken) is not null)
        {
            throw new DomainRuleException($"This draft waits for approval; withdraw it to change it.");
        }
    }

    /// <summary>Releasing directly is refused while a request waits, and where a site asks for approval.</summary>
    public static async Task EnsureDirectReleaseAsync(IReleaseApprovalRepository? approvals, string kind, Guid versionId, IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken)
    {
        if (approvals is null)
        {
            return;
        }

        await EnsureEditableAsync(approvals, versionId, cancellationToken);
        if ((await approvals.SitesRequiringAsync(kind, siteIds, cancellationToken)).Count > 0)
        {
            throw new DomainRuleException($"Approval is required before release here: submit the draft for approval.");
        }
    }
}

/// <summary>A site's settings (#138), on Configure → Site settings.</summary>
public sealed record GetReleaseApprovalSettingsQuery(Guid SiteId) : IQuery<ReleaseApprovalSettingsDto>;

public sealed record SetReleaseApprovalSettingsCommand(Guid SiteId, long ExpectedVersion, bool Processes, bool Boms, bool Specs) : ICommand<VersionResponse>;

/// <summary>Submits a draft for release where approval is required (#138); the draft then waits and doesn't change.</summary>
public sealed record SubmitReleaseCommand(string Kind, Guid DocumentId, Guid VersionId, long ExpectedVersion, string? Note) : ICommand<CreatedResponse>;

/// <summary>
/// Approves a waiting request, which releases the version (#138): by someone holding <c>releases.approve</c> at one of its sites (<paramref
/// name="ApproverSites"/>; null: every site) who didn't submit or edit it, with a comment.
/// </summary>
public sealed record ApproveReleaseCommand(Guid RequestId, string? Comment, IReadOnlyList<Guid>? ApproverSites) : ICommand<ReleaseDecisionResponse>;

/// <summary>Sends a waiting request back: the version is a draft again, with why.</summary>
public sealed record ReturnReleaseCommand(Guid RequestId, string? Comment, IReadOnlyList<Guid>? ApproverSites) : ICommand<ReleaseDecisionResponse>;

/// <summary>Takes back a request one submitted.</summary>
public sealed record WithdrawReleaseCommand(Guid RequestId) : ICommand<ReleaseDecisionResponse>;

/// <summary>Whether a document's release needs approval, and its requests (#138).</summary>
public sealed record GetReleaseStatusQuery(string Kind, Guid DocumentId, IReadOnlyList<Guid>? ApproverSites) : IQuery<ReleaseStatusDto>;

internal sealed class GetReleaseApprovalSettingsHandler(IReleaseApprovalReadStore store) : IQueryHandler<GetReleaseApprovalSettingsQuery, ReleaseApprovalSettingsDto>
{
    public Task<ReleaseApprovalSettingsDto> HandleAsync(GetReleaseApprovalSettingsQuery query, CancellationToken cancellationToken) =>
        store.GetSettingsAsync(query.SiteId, cancellationToken);
}

internal sealed class SetReleaseApprovalSettingsHandler(
    IUnitOfWork unitOfWork, IReleaseApprovalRepository approvals, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<SetReleaseApprovalSettingsCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetReleaseApprovalSettingsCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await approvals.GetSettingsForUpdateAsync(command.SiteId, cancellationToken);
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"The release approval settings were changed by someone else. Reload and try again.");
        }

        var after = new ReleaseApprovalSettings(command.SiteId, command.Processes, command.Boms, command.Specs);
        if (after == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.ReleaseApprovalSettingsChanged, "site", command.SiteId, new { Before = current.Value, After = after });
        var version = await approvals.UpdateSettingsAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SubmitReleaseHandler(
    IUnitOfWork unitOfWork, IReleaseApprovalRepository approvals, IEnumerable<IReleasableDocument> documents, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<SubmitReleaseCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(SubmitReleaseCommand command, CancellationToken cancellationToken)
    {
        var document = ReleaseDocuments.For(documents, command.Kind);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var draft = await document.LockAsync(command.DocumentId, command.VersionId, command.ExpectedVersion, cancellationToken);
        await ReleaseGate.EnsureEditableAsync(approvals, draft.VersionId, cancellationToken);
        var sites = await approvals.SitesRequiringAsync(command.Kind, draft.SiteIds, cancellationToken);
        if (sites.Count == 0)
        {
            throw new DomainRuleException($"Approval isn't required for {draft.Code}: release it directly.");
        }

        await document.EnsureReleasableAsync(draft, cancellationToken);
        var envelope = events.Create(EventTypes.ReleaseSubmitted, command.Kind, draft.DocumentId, new { draft.VersionId, draft.Number, command.Note });
        var request = ReleaseRequest.Submit(ids.NewId(), command.Kind, draft.DocumentId, draft.VersionId, sites, envelope.ActorId, envelope.OccurredAt, command.Note);
        await approvals.InsertAsync(request, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(request.Id, 0);
    }
}

internal sealed class ApproveReleaseHandler(
    IUnitOfWork unitOfWork, IReleaseApprovalRepository approvals, IEnumerable<IReleasableDocument> documents, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<ApproveReleaseCommand, ReleaseDecisionResponse>
{
    public async Task<ReleaseDecisionResponse> HandleAsync(ApproveReleaseCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (document, draft, request) = await ReleaseDocuments.LockAsync(approvals, documents, command.RequestId, command.ApproverSites, cancellationToken);
        var envelope = events.Create(EventTypes.ReleaseApproved, request.Kind, request.DocumentId, new { request.VersionId, draft.Number, Comment = command.Comment });
        var editors = await approvals.EditorsAsync(request.DocumentId, request.VersionId, document.DraftSavedEvents, cancellationToken);
        var approved = request.Approve(envelope.ActorId, editors, envelope.OccurredAt, command.Comment);
        await document.EnsureReleasableAsync(draft, cancellationToken);
        await approvals.UpdateAsync(approved, cancellationToken);
        var notes = await document.ReleaseAsync(draft, request.Note, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReleaseDecisionResponse(approved.Id, approved.Status, notes);
    }
}

internal sealed class ReturnReleaseHandler(
    IUnitOfWork unitOfWork, IReleaseApprovalRepository approvals, IEnumerable<IReleasableDocument> documents, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<ReturnReleaseCommand, ReleaseDecisionResponse>
{
    public async Task<ReleaseDecisionResponse> HandleAsync(ReturnReleaseCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (_, draft, request) = await ReleaseDocuments.LockAsync(approvals, documents, command.RequestId, command.ApproverSites, cancellationToken);
        var envelope = events.Create(EventTypes.ReleaseReturned, request.Kind, request.DocumentId, new { request.VersionId, draft.Number, Comment = command.Comment });
        var returned = request.Return(envelope.ActorId, envelope.OccurredAt, command.Comment);
        await approvals.UpdateAsync(returned, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReleaseDecisionResponse(returned.Id, returned.Status, []);
    }
}

internal sealed class WithdrawReleaseHandler(
    IUnitOfWork unitOfWork, IReleaseApprovalRepository approvals, IEnumerable<IReleasableDocument> documents, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<WithdrawReleaseCommand, ReleaseDecisionResponse>
{
    public async Task<ReleaseDecisionResponse> HandleAsync(WithdrawReleaseCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (_, draft, request) = await ReleaseDocuments.LockAsync(approvals, documents, command.RequestId, null, cancellationToken);
        var envelope = events.Create(EventTypes.ReleaseWithdrawn, request.Kind, request.DocumentId, new { request.VersionId, draft.Number });
        var withdrawn = request.Withdraw(envelope.ActorId, envelope.OccurredAt);
        await approvals.UpdateAsync(withdrawn, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReleaseDecisionResponse(withdrawn.Id, withdrawn.Status, []);
    }
}

internal sealed class GetReleaseStatusHandler(IReleaseApprovalReadStore store, IEnumerable<IReleasableDocument> documents, IRequestContext request)
    : IQueryHandler<GetReleaseStatusQuery, ReleaseStatusDto>
{
    public async Task<ReleaseStatusDto> HandleAsync(GetReleaseStatusQuery query, CancellationToken cancellationToken)
    {
        var document = ReleaseDocuments.For(documents, query.Kind);
        var sites = await store.SitesForAsync(query.Kind, query.DocumentId, cancellationToken);
        var requests = await store.ListAsync(query.Kind, query.DocumentId, cancellationToken);
        var list = new List<ReleaseRequestDto>();
        foreach (var (r, number, submittedBy, decidedBy) in requests)
        {
            var mayDecide = false;
            if (r.IsPending && r.SubmittedBy != request.ActorId && (query.ApproverSites is null || r.SiteIds.Any(query.ApproverSites.Contains)))
            {
                mayDecide = !(await store.EditorsAsync(r.DocumentId, r.VersionId, document.DraftSavedEvents, cancellationToken)).Contains(request.ActorId);
            }

            list.Add(new ReleaseRequestDto
            {
                Id = r.Id,
                Kind = r.Kind,
                DocumentId = r.DocumentId,
                VersionId = r.VersionId,
                VersionNumber = number,
                Status = r.Status,
                SubmittedAt = r.SubmittedAt,
                SubmittedByName = submittedBy,
                Note = r.Note,
                DecidedAt = r.DecidedAt,
                DecidedByName = decidedBy,
                DecisionNote = r.DecisionNote,
                MayDecide = mayDecide,
                MayWithdraw = r.IsPending && r.SubmittedBy == request.ActorId,
            });
        }

        return new ReleaseStatusDto { ApprovalRequired = await store.AnyRequiresAsync(query.Kind, sites, cancellationToken), Requests = list };
    }
}

/// <summary>Finding a kind's document and locking a request's document, then the request (#138).</summary>
internal static class ReleaseDocuments
{
    public static IReleasableDocument For(IEnumerable<IReleasableDocument> documents, string kind) =>
        documents.FirstOrDefault(d => d.Kind == kind) ?? throw new DomainRuleException($"'{kind}' isn't a kind of document that is released.");

    public static async Task<(IReleasableDocument Document, ReleasableDraft Draft, ReleaseRequest Request)> LockAsync(
        IReleaseApprovalRepository approvals, IEnumerable<IReleasableDocument> documents, Guid requestId, IReadOnlyList<Guid>? approverSites, CancellationToken cancellationToken)
    {
        var found = await approvals.GetAsync(requestId, cancellationToken) ?? throw new NotFoundException($"Release request {requestId} not found.");
        var document = For(documents, found.Kind);
        var draft = await document.LockAsync(found.DocumentId, found.VersionId, null, cancellationToken);
        var request = await approvals.GetForUpdateAsync(requestId, cancellationToken) ?? throw new NotFoundException($"Release request {requestId} not found.");
        if (approverSites is not null && !request.SiteIds.Any(approverSites.Contains))
        {
            throw new ForbiddenException($"You may not decide releases at the sites that asked for this approval.");
        }

        return (document, draft, request);
    }
}
