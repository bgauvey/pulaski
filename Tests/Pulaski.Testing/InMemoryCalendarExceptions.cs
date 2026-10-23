using Pulaski.Application.Abstractions;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Shifts;

namespace Pulaski.Testing;

/// <summary>In-memory calendar exceptions (D-043). The read side knows each one's site through <see cref="SiteOf"/>.</summary>
public sealed class InMemoryCalendarExceptions : ICalendarExceptionReadStore, ICalendarExceptionRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<CalendarExceptionRecord>> _rows = [];
    private long _next = 1;

    /// <summary>The site of a piece of equipment, for listing by site.</summary>
    public Func<Guid, Guid?> SiteOf { get; set; } = _ => null;

    public Versioned<CalendarExceptionRecord>? Get(Guid id)
    {
        lock (_lock)
        {
            return _rows.GetValueOrDefault(id);
        }
    }

    public Task<IReadOnlyList<CalendarExceptionDto>> ListAsync(Guid siteId, DateTimeOffset? from, bool includeRemoved, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<CalendarExceptionDto>>([.. _rows.Values
                .Where(r => SiteOf(r.Value.ScopeEquipmentId) == siteId && (from is null || r.Value.EndsAt > from) && (r.Value.IsActive || includeRemoved))
                .OrderBy(r => r.Value.StartsAt)
                .Select(r => new CalendarExceptionDto
                {
                    Id = r.Value.Id,
                    SiteId = siteId,
                    ScopeEquipmentId = r.Value.ScopeEquipmentId,
                    Kind = r.Value.Kind,
                    StartsAt = r.Value.StartsAt,
                    EndsAt = r.Value.EndsAt,
                    Note = r.Value.Note,
                    IsActive = r.Value.IsActive,
                    Version = r.Version,
                })]);
        }
    }

    public Task<Versioned<CalendarExceptionRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Get(id));

    public Task<long> InsertAsync(CalendarExceptionRecord exception, DateTimeOffset at, CancellationToken cancellationToken) => Save(exception);

    public Task<long> UpdateAsync(CalendarExceptionRecord exception, DateTimeOffset at, CancellationToken cancellationToken) => Save(exception);

    private Task<long> Save(CalendarExceptionRecord exception)
    {
        lock (_lock)
        {
            var version = ++_next;
            _rows[exception.Id] = new(exception, version);
            return Task.FromResult(version);
        }
    }
}
