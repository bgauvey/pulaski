using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.ReferenceData;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.WorkOrders;

public sealed record CreateWorkOrderCommand(
    Guid SiteId,
    string OrderNumber,
    Guid MaterialId,
    decimal PlannedQty,
    Guid UomId,
    Guid? TargetEquipmentId,
    DateTimeOffset? PlannedStart,
    DateTimeOffset? PlannedEnd,
    DateTimeOffset? DueAt,
    int Priority) : ICommand<CreatedResponse>;

public sealed record ChangeWorkOrderStatusCommand(
    Guid Id,
    long ExpectedVersion,
    string Status,
    string? Reason) : ICommand<VersionResponse>;

internal sealed class CreateWorkOrderHandler(
    IUnitOfWork unitOfWork,
    IWorkOrderRepository workOrders,
    IEquipmentRepository equipment,
    IMaterialRepository materials,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateWorkOrderCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateWorkOrderCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.SiteId != Guid.Empty, nameof(command.SiteId), "Required.")
            .RequireText(command.OrderNumber, nameof(command.OrderNumber), 50)
            .Require(command.MaterialId != Guid.Empty, nameof(command.MaterialId), "Required.")
            .Require(command.UomId != Guid.Empty, nameof(command.UomId), "Required.")
            .Require(command.PlannedQty > 0, nameof(command.PlannedQty), "Must be greater than zero.")
            .Require(
                command.PlannedStart is null || command.PlannedEnd is null || command.PlannedEnd > command.PlannedStart,
                nameof(command.PlannedEnd),
                "Must be after the planned start.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var site = await equipment.GetAsync(command.SiteId, RowLock.Share, cancellationToken);
        if (site?.Value.Level != EquipmentTypes.Site)
        {
            throw new DomainRuleException($"Site {command.SiteId} not found.");
        }

        if (!await materials.IsActiveAsync(command.MaterialId, cancellationToken))
        {
            throw new DomainRuleException($"Material {command.MaterialId} not found or inactive.");
        }

        if (command.TargetEquipmentId is { } target &&
            !await equipment.IsInSubtreeAsync(command.SiteId, target, cancellationToken))
        {
            throw new DomainRuleException($"Target equipment {target} is not in site {site.Value.Code}.");
        }

        var workOrder = WorkOrder.CreateLocal(
            ids.NewId(),
            command.SiteId,
            command.OrderNumber.Trim(),
            command.MaterialId,
            command.PlannedQty,
            command.UomId,
            command.TargetEquipmentId,
            command.PlannedStart,
            command.PlannedEnd,
            command.DueAt,
            command.Priority);

        var envelope = events.Create(EventTypes.WorkOrderCreated, "work_order", workOrder.Id, new { After = workOrder });
        var version = await workOrders.InsertAsync(workOrder, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(workOrder.Id, version);
    }
}

internal sealed class ChangeWorkOrderStatusHandler(
    IUnitOfWork unitOfWork,
    IWorkOrderRepository workOrders,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<ChangeWorkOrderStatusCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ChangeWorkOrderStatusCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(WorkOrderStatus.All.Contains(command.Status), nameof(command.Status), $"Unknown status '{command.Status}'.")
            .Require(command.Reason is null || command.Reason.Length <= 500, nameof(command.Reason), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var current = await workOrders.GetForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Work order {command.Id} not found.");

        // Check the version before the rule, so a stale screen reports a conflict, not a bad transition.
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"Work order {current.Value.OrderNumber} was changed by someone else. Reload and try again.");
        }

        var changed = current.Value.WithStatus(command.Status);

        var envelope = events.Create(
            EventTypes.WorkOrderStatusChanged,
            "work_order",
            changed.Id,
            new { Before = new { current.Value.Status }, After = new { changed.Status } },
            command.Reason);
        var version = await workOrders.UpdateStatusAsync(changed.Id, changed.Status, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}
