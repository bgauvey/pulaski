using System.Globalization;
using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Audit;

namespace Pulaski.Application.Audit;

/// <summary>
/// What the audit trail is searched by (#129): one thing's history (<see cref="SubjectId"/>), or an area (subject type), an action (event type,
/// or the start of one), who (user name or display name), text anywhere in the change, and a time window; newest first, a page at a time.
/// </summary>
public sealed record AuditFilter(
    Guid? SubjectId = null, string? Area = null, string? Action = null, string? Actor = null, string? Text = null, DateTimeOffset? From = null,
    DateTimeOffset? To = null, DateTimeOffset? BeforeRecordedAt = null, Guid? BeforeId = null, int Limit = 50);

/// <summary>An event as stored, for the audit trail.</summary>
public sealed record AuditRow(
    Guid Id, DateTimeOffset RecordedAt, DateTimeOffset OccurredAt, string Type, string Source, Guid Subject, string SubjectType, string ActorType,
    string ActorId, string? ActorName, string? Reason, string Data);

public interface IAuditReadStore
{
    /// <summary>Up to <see cref="AuditFilter.Limit"/> + 1 events matching, newest recorded first.</summary>
    Task<IReadOnlyList<AuditRow>> SearchAsync(AuditFilter filter, CancellationToken cancellationToken);
}

/// <summary>A page boundary of the audit trail (#129): the recorded time and id of the last event shown. Pure.</summary>
public static class AuditCursor
{
    public static string Of(AuditRow row) => $"{row.RecordedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{row.Id}";

    /// <summary>Reads a cursor back into the time and id the next page starts below.</summary>
    public static (DateTimeOffset RecordedAt, Guid Id)? Parse(string? cursor) =>
        cursor?.Split('|') is [var ticks, var id] && long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && Guid.TryParse(id, out var g)
            ? (new DateTimeOffset(t, TimeSpan.Zero), g)
            : null;
}

/// <summary>Searches the audit trail (#129). Without one thing named, it looks back 30 days at most unless told otherwise, and 92 at most.</summary>
public sealed record SearchAuditQuery(AuditFilter Filter) : IQuery<AuditPageDto>;

internal sealed class SearchAuditHandler(IAuditReadStore store, TimeProvider time) : IQueryHandler<SearchAuditQuery, AuditPageDto>
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(92);

    public async Task<AuditPageDto> HandleAsync(SearchAuditQuery query, CancellationToken cancellationToken)
    {
        var f = query.Filter;
        var now = time.GetUtcNow();
        var to = f.To ?? now;
        var from = f.From ?? (f.SubjectId is null ? to - DefaultWindow : DateTimeOffset.MinValue);
        if (f.SubjectId is null && to - from > MaxWindow)
        {
            throw ValidationException.Field("From", $"Look back at most 92 days, or choose one thing to see all its history.");
        }

        var filter = f with
        {
            From = from == DateTimeOffset.MinValue ? null : from,
            To = to,
            Area = Clean(f.Area),
            Action = Clean(f.Action),
            Actor = Clean(f.Actor),
            Text = Clean(f.Text),
            Limit = Math.Clamp(f.Limit, 1, 200),
        };
        var rows = await store.SearchAsync(filter, cancellationToken);
        var page = rows.Take(filter.Limit).ToList();
        return new AuditPageDto
        {
            Events = [.. page.Select(ToDto)],
            NextBefore = rows.Count > filter.Limit && page.Count > 0 ? AuditCursor.Of(page[^1]) : null,
        };
    }

    private static AuditEventDto ToDto(AuditRow r)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(r.Data) ? "{}" : r.Data);
        var data = doc.RootElement;
        var changes = AuditChanges.Diff(data);
        return new AuditEventDto
        {
            Id = r.Id,
            Type = r.Type,
            Action = AuditChanges.Action(r.Type),
            SubjectType = r.SubjectType,
            Subject = r.Subject,
            SubjectLabel = AuditChanges.Label(data),
            OccurredAt = r.OccurredAt,
            RecordedAt = r.RecordedAt,
            ActorType = r.ActorType,
            ActorId = r.ActorId,
            ActorName = r.ActorName,
            Source = r.Source,
            Reason = r.Reason,
            Changes = changes,
            Fields = changes.Count > 0 ? [] : AuditChanges.Fields(data),
            DataJson = JsonSerializer.Serialize(data, Indented),
        };
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
