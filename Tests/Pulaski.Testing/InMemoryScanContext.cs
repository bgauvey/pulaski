using Pulaski.Application.Inventory;

namespace Pulaski.Testing;

/// <summary>Scan context in memory (#135): the test sets the line, lots and containers it knows.</summary>
public sealed class InMemoryScanContext : IScanContextReadStore
{
    public Dictionary<Guid, ScanLine> Lines { get; } = [];

    public Dictionary<Guid, ScanLot> Lots { get; } = [];

    public Dictionary<Guid, ScanContainer> Containers { get; } = [];

    public Dictionary<Guid, IReadOnlyList<ScanComponent>> Components { get; } = [];

    public List<ScanJob> Jobs { get; } = [];

    public Task<ScanLine?> GetLineAsync(Guid lineId, CancellationToken cancellationToken) => Task.FromResult(Lines.GetValueOrDefault(lineId));

    public Task<IReadOnlyList<Pulaski.Contracts.Inventory.ScanLineOptionDto>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Pulaski.Contracts.Inventory.ScanLineOptionDto>>(
            [.. Lines.Values.Where(l => l.SiteId == siteId).Select(l => new Pulaski.Contracts.Inventory.ScanLineOptionDto(l.Id, l.Code, l.Code))]);

    public Task<ScanLot?> GetLotAsync(Guid lotId, CancellationToken cancellationToken) => Task.FromResult(Lots.GetValueOrDefault(lotId));

    public Task<ScanContainer?> GetContainerAsync(Guid containerId, CancellationToken cancellationToken) => Task.FromResult(Containers.GetValueOrDefault(containerId));

    public Task<IReadOnlyList<ScanComponent>> GetComponentsAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(Components.GetValueOrDefault(jobId) ?? []);

    public Task<IReadOnlyList<ScanJob>> FindJobsAsync(Guid siteId, string orderNumber, int? operationNumber, Guid lineId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ScanJob>>([.. Jobs]);
}
