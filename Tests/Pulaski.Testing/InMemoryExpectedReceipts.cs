using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Testing;

/// <summary>In-memory expected receipts (D-058).</summary>
public sealed class InMemoryExpectedReceipts : IExpectedReceiptRepository, IExpectedReceiptReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<ExpectedReceipt>> _receipts = [];
    private long _next = 1;

    /// <summary>Material codes and names for the list, as a test sets them; unit codes likewise.</summary>
    public Dictionary<Guid, string> Codes { get; } = [];

    public ExpectedReceipt Get(Guid id)
    {
        lock (_lock)
        {
            return _receipts[id].Value;
        }
    }

    public Task<Versioned<ExpectedReceipt>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_receipts.GetValueOrDefault(id));
        }
    }

    public Task<Versioned<ExpectedReceipt>?> FindByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_receipts.Values.FirstOrDefault(r => r.Value.ExternalSystem == externalSystem && r.Value.ExternalKey == externalKey));
        }
    }

    public Task<long> InsertAsync(ExpectedReceipt receipt, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Save(receipt));

    public Task<long> UpdateAsync(ExpectedReceipt receipt, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Save(receipt));

    public Task<IReadOnlyList<ExpectedReceiptDto>> ListAsync(Guid siteId, bool includeClosed, Guid? materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ExpectedReceiptDto>>([.. _receipts.Values
                .Where(r => r.Value.SiteId == siteId && (includeClosed || r.Value.IsOpen) && (materialId is null || r.Value.MaterialId == materialId))
                .OrderBy(r => r.Value.ExpectedAt)
                .Select(r => new ExpectedReceiptDto
                {
                    Id = r.Value.Id,
                    SiteId = r.Value.SiteId,
                    MaterialId = r.Value.MaterialId,
                    MaterialCode = Codes.GetValueOrDefault(r.Value.MaterialId, ""),
                    MaterialName = "",
                    Quantity = r.Value.Quantity,
                    UomId = r.Value.UomId,
                    UomCode = Codes.GetValueOrDefault(r.Value.UomId, ""),
                    ExpectedAt = r.Value.ExpectedAt,
                    Reference = r.Value.Reference,
                    Supplier = r.Value.Supplier,
                    Note = r.Value.Note,
                    Status = r.Value.Status,
                    ReceivedLotId = r.Value.ReceivedLotId,
                    ExternalSystem = r.Value.ExternalSystem,
                    ExternalKey = r.Value.ExternalKey,
                    CreatedAt = r.Value.CreatedAt,
                    CreatedBy = r.Value.CreatedBy,
                    Version = r.Version,
                })]);
        }
    }

    private long Save(ExpectedReceipt receipt)
    {
        lock (_lock)
        {
            var version = ++_next;
            _receipts[receipt.Id] = new(receipt, version);
            return version;
        }
    }
}
