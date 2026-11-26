using Pulaski.Contracts.Audit;

namespace Pulaski.Web.Services;

/// <summary>How the audit trail reads (#129). Pure.</summary>
public static class AuditView
{
    /// <summary>"Pat Lee (pat)" for a user with a name, else who recorded it: a user name, a service, a terminal.</summary>
    public static string Who(AuditEventDto e) => e.ActorName is { Length: > 0 } name && name != e.ActorId ? $"{name} ({e.ActorId})" : e.ActorId;

    /// <summary>The first 8 characters of an id, when nothing names the thing.</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>A day's start and end in UTC, for the search window: the whole of the days chosen.</summary>
    public static DateTimeOffset? Start(DateOnly? day) => day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;

    public static DateTimeOffset? End(DateOnly? day) => day is { } d ? new DateTimeOffset(d.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddTicks(-10) : null;
}
