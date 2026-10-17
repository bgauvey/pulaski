using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;

namespace Pulaski.Application.EquipmentModel;

public sealed record CreateEquipmentCommand(
    Guid? ParentId,
    string Code,
    string Name,
    string? Description,
    string Level,
    string EquipmentRole,
    bool TracksState,
    bool IsSchedulable,
    bool HoldsInventory,
    string? TimeZone,
    bool? CanRunJob = null,
    bool ComminglesLots = false,
    bool SingleMaterial = false) : ICommand<CreatedResponse>;

public sealed record UpdateEquipmentCommand(
    Guid Id,
    long ExpectedVersion,
    string Name,
    string? Description,
    bool TracksState,
    bool IsSchedulable,
    bool HoldsInventory,
    bool IsActive,
    bool? CanRunJob = null,
    bool? ComminglesLots = null,
    bool? SingleMaterial = null) : ICommand<VersionResponse>;

internal sealed class CreateEquipmentHandler(
    IUnitOfWork unitOfWork,
    IEquipmentRepository equipment,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateEquipmentCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateEquipmentCommand command, CancellationToken cancellationToken)
    {
        var timeZone = string.IsNullOrWhiteSpace(command.TimeZone) ? null : command.TimeZone.Trim();

        new Validator()
            .RequireText(command.Code, nameof(command.Code), 50)
            .RequireText(command.Name, nameof(command.Name))
            .Require(
                EquipmentTypes.IsValidLevelRole(command.Level, command.EquipmentRole),
                nameof(command.EquipmentRole),
                $"'{command.EquipmentRole}' is not a valid role for level '{command.Level}'.")
            .Require(
                command.ParentId.HasValue == EquipmentTypes.RequiresParent(command.Level),
                nameof(command.ParentId),
                EquipmentTypes.RequiresParent(command.Level) ? "Required." : "An enterprise has no parent.")
            .Require(
                !EquipmentTypes.RequiresTimeZone(command.Level) || timeZone is not null,
                nameof(command.TimeZone),
                "A site needs a time zone.")
            .Require(
                timeZone is null || TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _),
                nameof(command.TimeZone),
                $"'{timeZone}' is not a known IANA time zone.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        Equipment? parent = null;
        if (command.ParentId is { } parentId)
        {
            // A share lock stops the parent being changed or moved until this commits.
            parent = (await equipment.GetAsync(parentId, RowLock.Share, cancellationToken))?.Value
                ?? throw new DomainRuleException($"Parent equipment {parentId} not found.");
        }

        var created = Equipment.Create(
            ids.NewId(),
            parent,
            command.Code.Trim(),
            command.Name.Trim(),
            command.Description,
            command.Level,
            command.EquipmentRole,
            command.TracksState,
            command.IsSchedulable,
            command.HoldsInventory,
            timeZone,
            command.CanRunJob ?? command.IsSchedulable,
            command.ComminglesLots,
            command.SingleMaterial);

        var envelope = events.Create(EventTypes.EquipmentCreated, "equipment", created.Id, new { After = created });
        var version = await equipment.InsertAsync(created, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(created.Id, version);
    }
}

internal sealed class UpdateEquipmentHandler(
    IUnitOfWork unitOfWork,
    IEquipmentRepository equipment,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateEquipmentCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateEquipmentCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .RequireText(command.Name, nameof(command.Name))
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var current = await equipment.GetAsync(command.Id, RowLock.Update, cancellationToken)
            ?? throw new NotFoundException($"Equipment {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"Equipment {current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var updated = current.Value with
        {
            Name = command.Name.Trim(),
            Description = command.Description,
            TracksState = command.TracksState,
            IsSchedulable = command.IsSchedulable,
            CanRunJob = command.CanRunJob ?? current.Value.CanRunJob,
            HoldsInventory = command.HoldsInventory,
            ComminglesLots = command.ComminglesLots ?? current.Value.ComminglesLots,
            SingleMaterial = command.SingleMaterial ?? current.Value.SingleMaterial,
            IsActive = command.IsActive,
        };

        Equipment.EnsureStorageFlags(updated.HoldsInventory, updated.ComminglesLots, updated.SingleMaterial);
        await EnsureStockAllowsAsync(current.Value, updated, cancellationToken);

        var envelope = events.Create(
            EventTypes.EquipmentUpdated, "equipment", updated.Id, new { Before = current.Value, After = updated });
        var version = await equipment.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }

    // What the stock already there allows (D-031, D-032): nothing is stranded, separated or mixed by a change of settings.
    private async Task EnsureStockAllowsAsync(Equipment before, Equipment after, CancellationToken cancellationToken)
    {
        var closing = before.HoldsInventory && (!after.HoldsInventory || (before.IsActive && !after.IsActive));
        if (closing && await equipment.HoldsStockAsync(after.Id, cancellationToken))
        {
            throw new DomainRuleException(
                $"{after.Code} holds stock; move, use or write it off before it can {(after.IsActive ? "stop holding inventory" : "be deactivated")}.");
        }

        if (after.ComminglesLots && !before.ComminglesLots && await equipment.HoldsSeparateLotsAsync(after.Id, cancellationToken))
        {
            throw new DomainRuleException($"{after.Code} holds separate lots; merge them or move them elsewhere before their lots can be indistinguishable.");
        }

        if (after.SingleMaterial && !before.SingleMaterial && await equipment.CountMaterialsHeldAsync(after.Id, cancellationToken) > 1)
        {
            throw new DomainRuleException($"{after.Code} holds more than one material; empty all but one before it takes one material at a time.");
        }
    }
}
