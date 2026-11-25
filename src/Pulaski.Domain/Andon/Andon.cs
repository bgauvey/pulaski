using System.Text.RegularExpressions;

namespace Pulaski.Domain.Andon;

/// <summary>Where an andon call stands (#125, D-108). Values match <c>core.andon_call.status</c>.</summary>
public static class AndonStatuses
{
    /// <summary>Waiting for someone to come.</summary>
    public const string Open = "open";

    /// <summary>Someone has come and is on it.</summary>
    public const string Acknowledged = "acknowledged";

    /// <summary>Done, with what was done.</summary>
    public const string Resolved = "resolved";
}

/// <summary>
/// A kind of help a line can call for at a site (#125, D-108): maintenance, quality, materials, a supervisor. Its code is fixed once made; it is
/// switched off rather than deleted, since calls refer to it. A call still waiting after <see cref="EscalateAfterMinutes"/> is escalated.
/// </summary>
public sealed record AndonType(Guid Id, Guid SiteId, string Code, string Name, int EscalateAfterMinutes, int SortOrder, bool IsActive)
{
    public const int MaxName = 100;
    public const int MaxEscalateMinutes = 480;

    /// <exception cref="DomainRuleException">The code, name or escalation time can't be used.</exception>
    public static AndonType Create(Guid id, Guid siteId, string code, string name, int escalateAfterMinutes, int sortOrder)
    {
        var trimmed = (code ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(trimmed, "^[A-Z0-9][A-Z0-9_-]{0,19}$"))
        {
            throw new DomainRuleException("A call type's code is 1 to 20 letters, digits, '-' and '_', such as MAINT.");
        }

        return new AndonType(id, siteId, trimmed, "", 1, 0, true).Edit(name, escalateAfterMinutes, sortOrder, true);
    }

    /// <exception cref="DomainRuleException">The name or escalation time can't be used.</exception>
    public AndonType Edit(string name, int escalateAfterMinutes, int sortOrder, bool isActive)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > MaxName)
        {
            throw new DomainRuleException($"Give the call type a name of at most {MaxName} characters, such as Maintenance.");
        }

        if (escalateAfterMinutes is < 1 or > MaxEscalateMinutes)
        {
            throw new DomainRuleException($"A call escalates after 1 to {MaxEscalateMinutes} minutes without an answer.");
        }

        return this with { Name = trimmed, EscalateAfterMinutes = escalateAfterMinutes, SortOrder = sortOrder, IsActive = isActive };
    }
}

/// <summary>
/// A call for help from a line (#125, D-108): raised, acknowledged by whoever comes, resolved with what was done. One may be open per line and
/// type at a time (calling again while it waits is the same call). Never deleted.
/// </summary>
public sealed record AndonCall(
    Guid Id,
    Guid SiteId,
    Guid LineId,
    Guid TypeId,
    string Status,
    string? Note,
    DateTimeOffset RaisedAt,
    string RaisedBy,
    string? RaisedFrom,
    DateTimeOffset? AcknowledgedAt = null,
    string? AcknowledgedBy = null,
    DateTimeOffset? ResolvedAt = null,
    string? ResolvedBy = null,
    string? Resolution = null)
{
    public const int MaxText = 1000;

    public static AndonCall Raise(Guid id, Guid siteId, Guid lineId, AndonType type, string? note, DateTimeOffset at, string by, string? from)
    {
        if (!type.IsActive)
        {
            throw new DomainRuleException($"{type.Name} calls are switched off at this site.");
        }

        return new AndonCall(id, siteId, lineId, type.Id, AndonStatuses.Open, Text(note), at, by, from);
    }

    /// <summary>Someone has come: the call stops waiting.</summary>
    /// <exception cref="DomainRuleException">It is already acknowledged or resolved.</exception>
    public AndonCall Acknowledge(DateTimeOffset at, string by) => Status == AndonStatuses.Open
        ? this with { Status = AndonStatuses.Acknowledged, AcknowledgedAt = at, AcknowledgedBy = by }
        : throw (Status == AndonStatuses.Resolved
            ? new DomainRuleException("This call is resolved already.")
            : new DomainRuleException($"{AcknowledgedBy} is already on this call."));

    /// <summary>Done. Resolving a call nobody acknowledged acknowledges it at the same moment, by the same person.</summary>
    /// <exception cref="DomainRuleException">It is resolved already.</exception>
    public AndonCall Resolve(DateTimeOffset at, string by, string? resolution) => Status == AndonStatuses.Resolved
        ? throw new DomainRuleException("This call is resolved already.")
        : this with
        {
            Status = AndonStatuses.Resolved,
            AcknowledgedAt = AcknowledgedAt ?? at,
            AcknowledgedBy = AcknowledgedBy ?? by,
            ResolvedAt = at,
            ResolvedBy = by,
            Resolution = Text(resolution),
        };

    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is { Length: > MaxText })
        {
            throw new DomainRuleException($"Keep it to {MaxText} characters.");
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>How long a call has waited and whether it is escalated (#125, D-108): worked out on read, never stored. Pure.</summary>
public static class AndonRules
{
    /// <summary>Still waiting for someone past its type's escalation time.</summary>
    public static bool IsEscalated(AndonCall call, AndonType type, DateTimeOffset now) =>
        call.Status == AndonStatuses.Open && now - call.RaisedAt >= TimeSpan.FromMinutes(type.EscalateAfterMinutes);

    /// <summary>How long it waited for someone: to its acknowledgement, or to now while open.</summary>
    public static TimeSpan Waited(AndonCall call, DateTimeOffset now) => (call.AcknowledgedAt ?? now) - call.RaisedAt;
}
