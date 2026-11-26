namespace Pulaski.Domain.Quality;

/// <summary>Where a nonconformance report stands (#133, D-124): open → investigating → acting → closed.</summary>
public static class NcrStatus
{
    public const string Open = "open";
    public const string Investigating = "investigating";
    public const string Acting = "acting";
    public const string Closed = "closed";

    public static IReadOnlyList<string> All { get; } = [Open, Investigating, Acting, Closed];
}

public static class NcrSeverity
{
    public const string Minor = "minor";
    public const string Major = "major";
    public const string Critical = "critical";

    public static IReadOnlyList<string> All { get; } = [Minor, Major, Critical];
}

/// <summary>What an NCR was raised from (user's choice: anything that flags), or on its own.</summary>
public static class NcrSources
{
    public const string Manual = "manual";
    public const string LotHold = "lot_hold";
    public const string LotRejection = "lot_rejection";
    public const string QualityResult = "quality_result";
    public const string SpcAlert = "spc_alert";
    public const string Checklist = "checklist";
    public const string Complaint = "complaint";

    public static IReadOnlyList<string> All { get; } = [Manual, LotHold, LotRejection, QualityResult, SpcAlert, Checklist, Complaint];
}

/// <summary>What becomes of the lots concerned, decided on closing.</summary>
public static class NcrDispositions
{
    public const string UseAsIs = "use_as_is";
    public const string Rework = "rework";
    public const string Scrap = "scrap";
    public const string ReturnToSupplier = "return_to_supplier";
    public const string NotApplicable = "not_applicable";

    public static IReadOnlyList<string> All { get; } = [UseAsIs, Rework, Scrap, ReturnToSupplier, NotApplicable];
}

public static class NcrActionKinds
{
    public const string Corrective = "corrective";
    public const string Preventive = "preventive";

    public static IReadOnlyList<string> All { get; } = [Corrective, Preventive];
}

public static class NcrActionStatus
{
    public const string Open = "open";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// A nonconformance report (#133, D-124): what was found and how bad, where it came from, who owns it, its root cause (with up to five whys),
/// and, once closed, what becomes of the lots and why. Numbered <c>NCR-000042</c>.
/// </summary>
public sealed record Nonconformance(
    Guid Id, Guid SiteId, string Number, string Title, string Description, string Severity, string Status, string SourceKind, Guid? SourceId,
    DateTimeOffset? SourceAt, Guid? OwnerUserId, string RaisedBy, DateTimeOffset RaisedAt, string? RootCause = null, IReadOnlyList<string>? Whys = null,
    string? Disposition = null, string? ClosingNote = null, DateTimeOffset? ClosedAt = null, string? ClosedBy = null)
{
    public const int MaxTitle = 200;
    public const int MaxText = 4000;
    public const int MaxWhys = 5;

    public bool IsClosed => Status == NcrStatus.Closed;

    /// <exception cref="DomainRuleException">The title, description, severity or source isn't right.</exception>
    public static Nonconformance Raise(
        Guid id, Guid siteId, string number, string? title, string? description, string? severity, string? sourceKind, Guid? sourceId, DateTimeOffset? sourceAt,
        Guid? ownerUserId, string by, DateTimeOffset at)
    {
        if (!NcrSources.All.Contains(sourceKind))
        {
            throw new DomainRuleException($"'{sourceKind}' isn't something an NCR is raised from.");
        }

        if (sourceKind is not (NcrSources.Manual or NcrSources.Complaint) && sourceId is null)
        {
            throw new DomainRuleException($"Say which {UserMessage.Code(sourceKind!)} the NCR is raised from.");
        }

        var (t, d, s) = Describe(title, description, severity);
        return new(id, siteId, number, t, d, s, NcrStatus.Open, sourceKind!, sourceId, sourceAt, ownerUserId, by, at);
    }

    /// <summary>Its title, description, severity and owner, while it isn't closed.</summary>
    public Nonconformance Edit(string? title, string? description, string? severity, Guid? ownerUserId)
    {
        EnsureOpen();
        var (t, d, s) = Describe(title, description, severity);
        return this with { Title = t, Description = d, Severity = s, OwnerUserId = ownerUserId };
    }

    /// <summary>
    /// What the investigation found: the root cause and up to five whys. Saved, it is <c>investigating</c>; <paramref name="complete"/> (which needs the
    /// root cause) moves it on to <c>acting</c>.
    /// </summary>
    public Nonconformance Investigate(string? rootCause, IReadOnlyList<string>? whys, bool complete)
    {
        EnsureOpen();
        var cause = string.IsNullOrWhiteSpace(rootCause) ? null : rootCause.Trim();
        var asked = (whys ?? []).Select(w => (w ?? "").Trim()).Where(w => w.Length > 0).ToList();
        if (cause is { Length: > MaxText } || asked.Any(w => w.Length > 500))
        {
            throw new DomainRuleException($"Keep the root cause to {MaxText} characters and each why to 500.");
        }

        if (asked.Count > MaxWhys)
        {
            throw new DomainRuleException($"Ask why at most {MaxWhys} times.");
        }

        if (complete && cause is null)
        {
            throw new DomainRuleException($"Say what the root cause is before acting on it.");
        }

        var status = complete ? NcrStatus.Acting : Status == NcrStatus.Acting ? NcrStatus.Acting : NcrStatus.Investigating;
        return this with { RootCause = cause, Whys = asked, Status = status };
    }

    /// <summary>
    /// Closes it: acting, with its root cause, at least one action and none still open; what becomes of the lots, and a closing note.
    /// </summary>
    /// <exception cref="DomainRuleException">It isn't ready to close.</exception>
    public Nonconformance Close(string? disposition, string? note, IReadOnlyList<NcrAction> actions, string by, DateTimeOffset at)
    {
        EnsureOpen();
        if (Status != NcrStatus.Acting || RootCause is null)
        {
            throw new DomainRuleException($"{Number} is closed once its root cause is found and acted on.");
        }

        if (actions.Count == 0)
        {
            throw new DomainRuleException($"Add at least one corrective or preventive action before closing {Number}.");
        }

        if (actions.Count(a => a.Status == NcrActionStatus.Open) is var open and > 0)
        {
            throw new DomainRuleException(open == 1 ? $"1 action of {Number} isn't done yet." : $"{open} actions of {Number} aren't done yet.");
        }

        if (!NcrDispositions.All.Contains(disposition))
        {
            throw new DomainRuleException($"Say what becomes of the lots concerned.");
        }

        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return text is null || text.Length > MaxText
            ? throw new DomainRuleException($"Close it with a note of what was done, at most {MaxText} characters.")
            : this with { Status = NcrStatus.Closed, Disposition = disposition, ClosingNote = text, ClosedAt = at, ClosedBy = by };
    }

    public void EnsureOpen()
    {
        if (IsClosed)
        {
            throw new DomainRuleException($"{Number} is closed.");
        }
    }

    private static (string Title, string Description, string Severity) Describe(string? title, string? description, string? severity)
    {
        var t = (title ?? "").Trim();
        var d = (description ?? "").Trim();
        if (t.Length is 0 or > MaxTitle)
        {
            throw new DomainRuleException($"Give the NCR a title of at most {MaxTitle} characters.");
        }

        if (d.Length > MaxText)
        {
            throw new DomainRuleException($"Keep the description to {MaxText} characters.");
        }

        return NcrSeverity.All.Contains(severity)
            ? (t, d, severity!)
            : throw new DomainRuleException($"Choose how severe it is: minor, major or critical.");
    }
}

/// <summary>A corrective or preventive action of an NCR (#133): what is to be done, by whom, by when; done with what was done, or cancelled with why.</summary>
public sealed record NcrAction(
    Guid Id, Guid NcrId, string Kind, string Description, Guid? OwnerUserId, DateOnly? DueOn, string Status, DateTimeOffset? DoneAt = null,
    string? DoneBy = null, string? Note = null)
{
    public static NcrAction Add(Guid id, Guid ncrId, string? kind, string? description, Guid? ownerUserId, DateOnly? dueOn)
    {
        var text = (description ?? "").Trim();
        if (!NcrActionKinds.All.Contains(kind))
        {
            throw new DomainRuleException($"An action is corrective or preventive.");
        }

        return text.Length is 0 or > 1000
            ? throw new DomainRuleException($"Say what is to be done, in at most 1000 characters.")
            : new(id, ncrId, kind!, text, ownerUserId, dueOn, NcrActionStatus.Open);
    }

    /// <summary>Overdue: still open after its due day (the site's).</summary>
    public bool IsOverdue(DateOnly today) => Status == NcrActionStatus.Open && DueOn is { } due && due < today;

    public NcrAction Complete(string? note, string by, DateTimeOffset at) => Finish(NcrActionStatus.Done, note, by, at, noteRequired: true);

    public NcrAction Cancel(string? note, string by, DateTimeOffset at) => Finish(NcrActionStatus.Cancelled, note, by, at, noteRequired: true);

    private NcrAction Finish(string status, string? note, string by, DateTimeOffset at, bool noteRequired)
    {
        if (Status != NcrActionStatus.Open)
        {
            throw new DomainRuleException($"That action is {UserMessage.Code(Status)} already.");
        }

        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return noteRequired && text is null || text is { Length: > 1000 }
            ? throw new DomainRuleException($"Say what was done (or why not), in at most 1000 characters.")
            : this with { Status = status, DoneAt = at, DoneBy = by, Note = text };
    }
}
