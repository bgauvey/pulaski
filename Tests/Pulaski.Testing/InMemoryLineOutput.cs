using Pulaski.Application.Abstractions;
using Pulaski.Application.Execution;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Testing;

/// <summary>In-memory line report defaults (#122): the test adds the lines, locations and printers it uses.</summary>
public sealed class InMemoryLineOutput : ILineOutputRepository
{
    private long _version;

    public Dictionary<Guid, (LineOutput Line, long Version)> Lines { get; } = [];

    public HashSet<(Guid Site, Guid Location)> Locations { get; } = [];

    public HashSet<(Guid Site, Guid Printer)> Printers { get; } = [];

    public Task<Versioned<LineOutput>?> GetForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Lines.TryGetValue(equipmentId, out var l) ? new Versioned<LineOutput>(l.Line, l.Version) : null);

    public Task<bool> IsLocationAsync(Guid siteId, Guid locationId, CancellationToken cancellationToken) => Task.FromResult(Locations.Contains((siteId, locationId)));

    public Task<bool> IsPrinterAsync(Guid siteId, Guid printerId, CancellationToken cancellationToken) => Task.FromResult(Printers.Contains((siteId, printerId)));

    public Task<long> UpdateAsync(LineOutput output, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Lines[output.EquipmentId] = (output, ++_version);
        return Task.FromResult(_version);
    }
}

/// <summary>Records which closed lots a test's commands asked to print (#122), and answers with one note each time.</summary>
public sealed class RecordingLineLabels : ILineLabels
{
    public List<(Guid LineId, IReadOnlyList<Guid> LotIds)> Printed { get; } = [];

    public Task<IReadOnlyList<InventoryWarningDto>> PrintClosedLotsAsync(Guid lineId, IReadOnlyList<Guid> lotIds, CancellationToken cancellationToken)
    {
        Printed.Add((lineId, lotIds));
        return Task.FromResult<IReadOnlyList<InventoryWarningDto>>([new InventoryWarningDto("label_printed", $"{lotIds.Count} labels printed.")]);
    }
}
