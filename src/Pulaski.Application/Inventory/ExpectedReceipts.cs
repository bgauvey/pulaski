using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Write side for expected receipts (D-058); runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IExpectedReceiptRepository
{
    Task<Versioned<ExpectedReceipt>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<Versioned<ExpectedReceipt>?> FindByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken);

    Task<long> InsertAsync(ExpectedReceipt receipt, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(ExpectedReceipt receipt, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side for expected receipts (D-058).</summary>
public interface IExpectedReceiptReadStore
{
    /// <summary>The site's expected receipts by expected time; only open ones unless <paramref name="includeClosed"/>.</summary>
    Task<IReadOnlyList<ExpectedReceiptDto>> ListAsync(Guid siteId, bool includeClosed, Guid? materialId, CancellationToken cancellationToken);
}

public sealed record ListExpectedReceiptsQuery(Guid SiteId, bool IncludeClosed = false, Guid? MaterialId = null) : IQuery<IReadOnlyList<ExpectedReceiptDto>>;

public sealed record CreateExpectedReceiptCommand(
    Guid SiteId, Guid MaterialId, decimal Quantity, Guid UomId, DateTimeOffset ExpectedAt, string? Reference, string? Supplier, string? Note)
    : ICommand<CreatedResponse>;

public sealed record UpdateExpectedReceiptCommand(
    Guid Id, long ExpectedVersion, decimal Quantity, Guid UomId, DateTimeOffset ExpectedAt, string? Reference, string? Supplier, string? Note)
    : ICommand<VersionResponse>;

public sealed record CancelExpectedReceiptCommand(Guid Id, long ExpectedVersion) : ICommand<VersionResponse>;

/// <summary>One expected receipt from the ERP, matched on its system and key (D-058); see <see cref="ImportExpectedReceiptRequest"/>.</summary>
public sealed record ImportExpectedReceiptCommand(
    Guid SiteId, string ExternalSystem, string ExternalKey, string MaterialCode, decimal Quantity, string UomCode, DateTimeOffset ExpectedAt,
    string? Reference, string? Supplier, bool Cancelled) : ICommand<ImportExpectedReceiptResponse>;

internal static class ExpectedReceiptRules
{
    public const int MaxText = 100;

    public static Validator RequireFields(this Validator validator, decimal quantity, Guid uomId, string? reference, string? supplier, string? note) =>
        validator
            .Require(quantity > 0, "Quantity", "Must be greater than zero.")
            .Require(uomId != Guid.Empty, "UomId", "Required.")
            .Require(reference is null || reference.Length <= MaxText, "Reference", $"At most {MaxText} characters.")
            .Require(supplier is null || supplier.Length <= 200, "Supplier", "At most 200 characters.")
            .Require(note is null || note.Length <= 1000, "Note", "At most 1000 characters.");

    /// <summary>The material, active; and the unit, which must convert to its base unit so the check can count it.</summary>
    public static async Task<InventoryMaterial> MaterialAsync(
        IInventoryRepository inventory, IUnitRepository units, Guid materialId, Guid uomId, CancellationToken cancellationToken)
    {
        var material = await InventoryRules.MaterialAsync(inventory, materialId, cancellationToken);
        if (!material.IsActive)
        {
            throw new DomainRuleException($"{material.Code} is inactive.");
        }

        await InventoryRules.ToBaseAsync(units, material, 1m, uomId, cancellationToken);
        return material;
    }

    public static async Task EnsureSiteAsync(IInventoryRepository inventory, Guid siteId, CancellationToken cancellationToken)
    {
        if (!await inventory.IsSiteAsync(siteId, cancellationToken))
        {
            throw new DomainRuleException("An expected receipt belongs to a site; choose one.");
        }
    }

    public static async Task<Versioned<ExpectedReceipt>> GetAsync(IExpectedReceiptRepository receipts, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await receipts.GetForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Expected receipt {id} not found.");
        return current.Version == expectedVersion
            ? current
            : throw new ConflictException($"{current.Value.Reference ?? "The expected receipt"} was changed by someone else. Reload and try again.");
    }
}

internal sealed class ListExpectedReceiptsHandler(IExpectedReceiptReadStore store) : IQueryHandler<ListExpectedReceiptsQuery, IReadOnlyList<ExpectedReceiptDto>>
{
    public Task<IReadOnlyList<ExpectedReceiptDto>> HandleAsync(ListExpectedReceiptsQuery query, CancellationToken cancellationToken) =>
        store.ListAsync(query.SiteId, query.IncludeClosed, query.MaterialId, cancellationToken);
}

internal sealed class CreateExpectedReceiptHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IExpectedReceiptRepository receipts,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    IRequestContext request,
    TimeProvider timeProvider) : ICommandHandler<CreateExpectedReceiptCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateExpectedReceiptCommand command, CancellationToken cancellationToken)
    {
        var reference = InventoryRules.Text(command.Reference);
        var supplier = InventoryRules.Text(command.Supplier);
        var note = InventoryRules.Text(command.Note);
        new Validator().RequireFields(command.Quantity, command.UomId, reference, supplier, note).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        await ExpectedReceiptRules.EnsureSiteAsync(inventory, command.SiteId, cancellationToken);
        await ExpectedReceiptRules.MaterialAsync(inventory, units, command.MaterialId, command.UomId, cancellationToken);
        var receipt = new ExpectedReceipt(
            ids.NewId(), command.SiteId, command.MaterialId, command.Quantity, command.UomId, command.ExpectedAt, reference, supplier, note,
            ExpectedReceipt.Open, null, null, null, timeProvider.GetUtcNow(), request.ActorId);
        var envelope = events.Create(EventTypes.ExpectedReceiptCreated, "expected_receipt", receipt.Id, new { After = receipt });
        var version = await receipts.InsertAsync(receipt, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(receipt.Id, version);
    }
}

internal sealed class UpdateExpectedReceiptHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IExpectedReceiptRepository receipts,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateExpectedReceiptCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateExpectedReceiptCommand command, CancellationToken cancellationToken)
    {
        var reference = InventoryRules.Text(command.Reference);
        var supplier = InventoryRules.Text(command.Supplier);
        var note = InventoryRules.Text(command.Note);
        new Validator().RequireFields(command.Quantity, command.UomId, reference, supplier, note).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await ExpectedReceiptRules.GetAsync(receipts, command.Id, command.ExpectedVersion, cancellationToken);
        if (command.UomId != current.Value.UomId)
        {
            await ExpectedReceiptRules.MaterialAsync(inventory, units, current.Value.MaterialId, command.UomId, cancellationToken);
        }

        var updated = current.Value.Change(command.Quantity, command.UomId, command.ExpectedAt, reference, supplier, note);
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.ExpectedReceiptUpdated, "expected_receipt", updated.Id, new { Before = current.Value, After = updated });
        var version = await receipts.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class CancelExpectedReceiptHandler(
    IUnitOfWork unitOfWork,
    IExpectedReceiptRepository receipts,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<CancelExpectedReceiptCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(CancelExpectedReceiptCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await ExpectedReceiptRules.GetAsync(receipts, command.Id, command.ExpectedVersion, cancellationToken);
        var cancelled = current.Value.Cancel();
        var envelope = events.Create(EventTypes.ExpectedReceiptCancelled, "expected_receipt", cancelled.Id, new { Before = current.Value, After = cancelled });
        var version = await receipts.UpdateAsync(cancelled, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class ImportExpectedReceiptHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IExpectedReceiptRepository receipts,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    IRequestContext request,
    TimeProvider timeProvider) : ICommandHandler<ImportExpectedReceiptCommand, ImportExpectedReceiptResponse>
{
    public async Task<ImportExpectedReceiptResponse> HandleAsync(ImportExpectedReceiptCommand command, CancellationToken cancellationToken)
    {
        var system = command.ExternalSystem.Trim();
        var key = command.ExternalKey.Trim();
        var reference = InventoryRules.Text(command.Reference);
        var supplier = InventoryRules.Text(command.Supplier);
        new Validator()
            .RequireText(system, "ExternalSystem", 100)
            .RequireText(key, "ExternalKey", 200)
            .RequireText(command.MaterialCode, "MaterialCode", 50)
            .RequireText(command.UomCode, "UomCode", 20)
            .Require(command.Quantity > 0, "Quantity", "Must be greater than zero.")
            .Require(reference is null || reference.Length <= ExpectedReceiptRules.MaxText, "Reference", $"At most {ExpectedReceiptRules.MaxText} characters.")
            .Require(supplier is null || supplier.Length <= 200, "Supplier", "At most 200 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await receipts.FindByExternalKeyForUpdateAsync(system, key, cancellationToken);

        // The ERP's word on a closed one comes too late: it was received or cancelled here, and stays so.
        if (current is { Value.IsOpen: false })
        {
            return new ImportExpectedReceiptResponse(current.Value.Id, current.Version, Created: false, Changed: false);
        }

        await ExpectedReceiptRules.EnsureSiteAsync(inventory, command.SiteId, cancellationToken);
        var materialId = await inventory.FindMaterialIdByCodeAsync(command.MaterialCode.Trim(), cancellationToken)
            ?? throw new DomainRuleException($"There is no material {command.MaterialCode.Trim()}.");
        var uom = await inventory.FindUomIdByCodeAsync(command.UomCode.Trim(), cancellationToken)
            ?? throw new DomainRuleException($"There is no unit {command.UomCode.Trim()}.");
        await ExpectedReceiptRules.MaterialAsync(inventory, units, materialId, uom, cancellationToken);

        if (current is null)
        {
            if (command.Cancelled)
            {
                throw new DomainRuleException($"{system} {key} is cancelled and Pulaski hasn't seen it; there is nothing to cancel.");
            }

            var receipt = new ExpectedReceipt(
                ids.NewId(), command.SiteId, materialId, command.Quantity, uom, command.ExpectedAt, reference, supplier, null,
                ExpectedReceipt.Open, null, system, key, timeProvider.GetUtcNow(), request.ActorId);
            var created = events.Create(EventTypes.ExpectedReceiptCreated, "expected_receipt", receipt.Id, new { After = receipt }, $"{system} import");
            var version = await receipts.InsertAsync(receipt, created.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(created, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ImportExpectedReceiptResponse(receipt.Id, version, Created: true, Changed: true);
        }

        if (current.Value.SiteId != command.SiteId || current.Value.MaterialId != materialId)
        {
            throw new DomainRuleException($"{system} {key} is for another site or material in Pulaski; send it as a new key.");
        }

        var updated = command.Cancelled
            ? current.Value.Cancel()
            : current.Value.Change(command.Quantity, uom, command.ExpectedAt, reference, supplier, current.Value.Note);
        if (updated == current.Value)
        {
            return new ImportExpectedReceiptResponse(current.Value.Id, current.Version, Created: false, Changed: false);
        }

        var envelope = events.Create(
            command.Cancelled ? EventTypes.ExpectedReceiptCancelled : EventTypes.ExpectedReceiptUpdated, "expected_receipt", updated.Id,
            new { Before = current.Value, After = updated }, $"{system} import");
        var newVersion = await receipts.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ImportExpectedReceiptResponse(updated.Id, newVersion, Created: false, Changed: true);
    }
}

/// <summary>Closing an expected receipt by receiving a lot against it (D-058), inside the receipt's transaction.</summary>
internal static class ExpectedReceiving
{
    public static async Task CloseAsync(
        IExpectedReceiptRepository receipts, IEventLog eventLog, IEventFactory events, Guid expectedReceiptId, Lot lot, CancellationToken cancellationToken)
    {
        var current = await receipts.GetForUpdateAsync(expectedReceiptId, cancellationToken)
            ?? throw new NotFoundException($"Expected receipt {expectedReceiptId} not found.");
        if (current.Value.SiteId != lot.SiteId || current.Value.MaterialId != lot.MaterialId)
        {
            throw new DomainRuleException($"{current.Value.Reference ?? "That expected receipt"} is for another material or site.");
        }

        var received = current.Value.Receive(lot.Id);
        var envelope = events.Create(EventTypes.ExpectedReceiptReceived, "expected_receipt", received.Id, new { Before = current.Value, After = received, lot.LotNumber });
        await receipts.UpdateAsync(received, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
    }
}
