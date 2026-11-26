namespace Pulaski.Domain.Approvals;

/// <summary>What a release approval is for (#138, D-122).</summary>
public static class ReleaseKinds
{
    public const string Process = "process";
    public const string Bom = "bom";
    public const string Spec = "spec";

    public static IReadOnlyList<string> All { get; } = [Process, Bom, Spec];
}

/// <summary>A release request's state: waiting, approved (and so released), sent back, or withdrawn by who submitted it.</summary>
public static class ReleaseRequestStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Returned = "returned";
    public const string Withdrawn = "withdrawn";
}

/// <summary>A site's choice (#138): which kinds of document need a second person's approval to be released.</summary>
public sealed record ReleaseApprovalSettings(Guid SiteId, bool Processes, bool Boms, bool Specs)
{
    public static ReleaseApprovalSettings Off(Guid siteId) => new(siteId, false, false, false);

    public bool Requires(string kind) => kind switch
    {
        ReleaseKinds.Process => Processes,
        ReleaseKinds.Bom => Boms,
        ReleaseKinds.Spec => Specs,
        _ => false,
    };
}

/// <summary>
/// A draft submitted for release (#138): for one version of a process, BOM or specification, at the sites whose setting asked for it. Once
/// decided it is fixed; the next submission is a new request. While it waits, the draft doesn't change.
/// </summary>
public sealed record ReleaseRequest(
    Guid Id, string Kind, Guid DocumentId, Guid VersionId, IReadOnlyList<Guid> SiteIds, string Status, string SubmittedBy, DateTimeOffset SubmittedAt,
    string? Note, string? DecidedBy = null, DateTimeOffset? DecidedAt = null, string? DecisionNote = null)
{
    public const int MaxText = 1000;

    public bool IsPending => Status == ReleaseRequestStatus.Pending;

    public static ReleaseRequest Submit(Guid id, string kind, Guid documentId, Guid versionId, IReadOnlyList<Guid> siteIds, string by, DateTimeOffset at, string? note) =>
        Text(note) is { Length: > MaxText }
            ? throw new DomainRuleException($"Keep the note to {MaxText} characters.")
            : new(id, kind, documentId, versionId, siteIds, ReleaseRequestStatus.Pending, by, at, Text(note));

    /// <summary>
    /// Approves it, which releases the version: by someone other than who submitted it or edited the draft (<paramref name="editors"/>), with a
    /// comment.
    /// </summary>
    /// <exception cref="DomainRuleException">It isn't waiting, the actor submitted or edited it, or there is no comment.</exception>
    public ReleaseRequest Approve(string actor, IReadOnlyCollection<string> editors, DateTimeOffset at, string? comment)
    {
        EnsurePending();
        if (string.Equals(actor, SubmittedBy, StringComparison.Ordinal) || editors.Contains(actor))
        {
            throw new DomainRuleException($"Someone who didn't edit or submit the draft approves it.");
        }

        return this with { Status = ReleaseRequestStatus.Approved, DecidedBy = actor, DecidedAt = at, DecisionNote = Comment(comment) };
    }

    /// <summary>Sends it back to the editors as a draft again, saying why.</summary>
    /// <exception cref="DomainRuleException">It isn't waiting, the actor submitted it (they withdraw it instead), or there is no comment.</exception>
    public ReleaseRequest Return(string actor, DateTimeOffset at, string? comment)
    {
        EnsurePending();
        if (string.Equals(actor, SubmittedBy, StringComparison.Ordinal))
        {
            throw new DomainRuleException($"You submitted it: withdraw it instead.");
        }

        return this with { Status = ReleaseRequestStatus.Returned, DecidedBy = actor, DecidedAt = at, DecisionNote = Comment(comment) };
    }

    /// <summary>Takes it back by who submitted it, so the draft can be changed.</summary>
    /// <exception cref="DomainRuleException">It isn't waiting, or the actor didn't submit it.</exception>
    public ReleaseRequest Withdraw(string actor, DateTimeOffset at)
    {
        EnsurePending();
        return string.Equals(actor, SubmittedBy, StringComparison.Ordinal)
            ? this with { Status = ReleaseRequestStatus.Withdrawn, DecidedBy = actor, DecidedAt = at }
            : throw new DomainRuleException($"Only who submitted it withdraws it; an approver sends it back.");
    }

    private void EnsurePending()
    {
        if (!IsPending)
        {
            throw new DomainRuleException($"That request is {UserMessage.Code(Status)} already.");
        }
    }

    private static string Comment(string? comment) =>
        Text(comment) is not { } text ? throw new DomainRuleException($"Say why, in a comment.")
        : text.Length > MaxText ? throw new DomainRuleException($"Keep the comment to {MaxText} characters.")
        : text;

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
