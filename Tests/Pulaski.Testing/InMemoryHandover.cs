using Pulaski.Application.Handover;
using Pulaski.Contracts.Handover;

namespace Pulaski.Testing;

/// <summary>In-memory shift handover (#124): the test adds lines; entries are kept as written.</summary>
public sealed class InMemoryHandover : IHandoverRepository, IHandoverReadStore
{
    public Dictionary<Guid, HandoverLine> Lines { get; } = [];

    public Dictionary<Guid, HandoverEntry> Entries { get; } = [];

    public Task<HandoverLine?> GetLineAsync(Guid lineId, CancellationToken cancellationToken) => Task.FromResult(Lines.GetValueOrDefault(lineId));

    public Task<HandoverEntry?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Entries.GetValueOrDefault(id));

    public Task InsertAsync(HandoverEntry entry, CancellationToken cancellationToken)
    {
        Entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task CloseAsync(HandoverEntry entry, CancellationToken cancellationToken) => InsertAsync(entry, cancellationToken);

    public Task<IReadOnlyList<(Guid Id, string Code, string? Name)>> ListLinesAsync(Guid siteId, Guid? lineId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(Guid Id, string Code, string? Name)>>(
            [.. Lines.Values.Where(l => l.SiteId == siteId && l.CanRunJob && l.IsActive && (lineId is null || l.Id == lineId)).OrderBy(l => l.Code, StringComparer.Ordinal)
                .Select(l => (l.Id, l.Code, (string?)null))]);

    public Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult<string?>("UTC");

    public Task<IReadOnlyList<HandoverEntryDto>> ListEntriesAsync(IReadOnlyCollection<Guid> lineIds, DateOnly fromDate, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HandoverEntryDto>>(
            [.. Entries.Values
                .Where(e => lineIds.Contains(e.LineId) && (e.ProductionDate >= fromDate || (e.Kind == HandoverKinds.OpenItem && e.ClosedAt is null)))
                .OrderBy(e => e.WrittenAt)
                .Select(e => new HandoverEntryDto
                {
                    Id = e.Id,
                    LineId = e.LineId,
                    Kind = e.Kind,
                    Body = e.Body,
                    ProductionDate = e.ProductionDate,
                    ShiftCode = e.ShiftCode,
                    WrittenAt = e.WrittenAt,
                    WrittenBy = e.WrittenBy,
                    ClosedAt = e.ClosedAt,
                    ClosedBy = e.ClosedBy,
                    ClosingNote = e.ClosingNote,
                })]);
}
