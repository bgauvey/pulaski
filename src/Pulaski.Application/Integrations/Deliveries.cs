using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Integrations;
using Pulaski.Domain;
using Pulaski.Domain.Integrations;

namespace Pulaski.Application.Integrations;

/// <summary>A delivery as resolving it needs it (D-061).</summary>
public sealed record DeliveryRecord(Guid Id, string Sink, Guid EventId, string EventType, Guid Subject, string Status, int Attempts);

/// <summary>Counts for a sink's status (D-061).</summary>
public sealed record DeliveryCounts(int Pending, int Failed, int Held, int DeliveredLastDay, DateTimeOffset? OldestPendingAt, DateTimeOffset? LastDeliveredAt);

/// <summary>The ERP feed's settings as screens may see them: never its address in full or its secret.</summary>
public sealed record DeliveryTarget(string Sink, bool Enabled, string? TargetHost, IReadOnlyList<string> EventTypes);

/// <summary>Operators' side of outbound deliveries (D-061); runs inside the current <see cref="IUnitOfWork"/> for changes.</summary>
public interface IDeliveryStore
{
    Task<IReadOnlyList<DeliveryDto>> ListAsync(string sink, string? status, int limit, CancellationToken cancellationToken);

    Task<DeliveryCounts> CountAsync(string sink, DateTimeOffset now, CancellationToken cancellationToken);

    Task<DeliveryRecord?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Sets a delivery's status, due now, with who resolved it and why.</summary>
    Task ResolveAsync(Guid id, string status, string actor, string? note, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every failed delivery of the sink back to pending, due now; returns how many.</summary>
    Task<int> RetryFailedAsync(string sink, string actor, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Where deliveries go and which events (D-061), from the API's configuration.</summary>
public interface IDeliveryTargets
{
    DeliveryTarget Erp { get; }
}

public sealed record GetDeliveryStatusQuery : IQuery<DeliveryStatusDto>;

public sealed record ListDeliveriesQuery(string? Status = null, int Limit = 200) : IQuery<IReadOnlyList<DeliveryDto>>;

/// <summary>Tries a failed delivery again now; its attempts carry on counting.</summary>
public sealed record RetryDeliveryCommand(Guid Id) : ICommand<RetryDeliveriesResponse>;

/// <summary>Tries every failed delivery to the ERP again now.</summary>
public sealed record RetryFailedDeliveriesCommand : ICommand<RetryDeliveriesResponse>;

/// <summary>
/// Gives up on a delivery, with a reason (D-061): someone has put it right in the ERP by hand, or it should never go. It stops holding
/// back the subject's later events. The event itself is untouched.
/// </summary>
public sealed record SkipDeliveryCommand(Guid Id, string Reason) : ICommand<RetryDeliveriesResponse>;

internal sealed class GetDeliveryStatusHandler(IDeliveryStore store, IDeliveryTargets targets, TimeProvider time) : IQueryHandler<GetDeliveryStatusQuery, DeliveryStatusDto>
{
    public async Task<DeliveryStatusDto> HandleAsync(GetDeliveryStatusQuery query, CancellationToken cancellationToken)
    {
        var target = targets.Erp;
        var counts = await store.CountAsync(target.Sink, time.GetUtcNow(), cancellationToken);
        return new DeliveryStatusDto
        {
            Sink = target.Sink,
            Enabled = target.Enabled,
            TargetHost = target.TargetHost,
            EventTypes = target.EventTypes,
            Pending = counts.Pending,
            Failed = counts.Failed,
            Held = counts.Held,
            DeliveredLastDay = counts.DeliveredLastDay,
            OldestPendingAt = counts.OldestPendingAt,
            LastDeliveredAt = counts.LastDeliveredAt,
        };
    }
}

internal sealed class ListDeliveriesHandler(IDeliveryStore store, IDeliveryTargets targets) : IQueryHandler<ListDeliveriesQuery, IReadOnlyList<DeliveryDto>>
{
    private static readonly string[] Statuses = [DeliveryStatuses.Pending, DeliveryStatuses.Delivered, DeliveryStatuses.Failed, DeliveryStatuses.Skipped];

    public Task<IReadOnlyList<DeliveryDto>> HandleAsync(ListDeliveriesQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.Status is null || Statuses.Contains(query.Status), "Status", "Pending, delivered, failed or skipped.")
            .Require(query.Limit is > 0 and <= 1000, "Limit", "Between 1 and 1000.")
            .ThrowIfInvalid();
        return store.ListAsync(targets.Erp.Sink, query.Status, query.Limit, cancellationToken);
    }
}

internal sealed class RetryDeliveryHandler(IUnitOfWork unitOfWork, IDeliveryStore store, IEventLog eventLog, IEventFactory events, IRequestContext request, TimeProvider time)
    : ICommandHandler<RetryDeliveryCommand, RetryDeliveriesResponse>
{
    public async Task<RetryDeliveriesResponse> HandleAsync(RetryDeliveryCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var delivery = await store.GetForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Delivery {command.Id} not found.");
        if (delivery.Status != DeliveryStatuses.Failed)
        {
            throw new DomainRuleException($"That delivery is {delivery.Status}; only a failed one is retried.");
        }

        var envelope = events.Create(EventTypes.DeliveryRetried, "delivery", delivery.Id, new { delivery.Sink, delivery.EventId, delivery.EventType, delivery.Attempts });
        await store.ResolveAsync(delivery.Id, DeliveryStatuses.Pending, request.ActorId, null, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RetryDeliveriesResponse(1);
    }
}

internal sealed class RetryFailedDeliveriesHandler(IUnitOfWork unitOfWork, IDeliveryStore store, IDeliveryTargets targets, IEventLog eventLog, IEventFactory events, IRequestContext request, TimeProvider time)
    : ICommandHandler<RetryFailedDeliveriesCommand, RetryDeliveriesResponse>
{
    public async Task<RetryDeliveriesResponse> HandleAsync(RetryFailedDeliveriesCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var retried = await store.RetryFailedAsync(targets.Erp.Sink, request.ActorId, time.GetUtcNow(), cancellationToken);
        if (retried > 0)
        {
            var envelope = events.Create(EventTypes.DeliveryRetried, "delivery", Guid.Empty, new { targets.Erp.Sink, Retried = retried, All = true });
            await eventLog.AppendAsync(envelope, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RetryDeliveriesResponse(retried);
    }
}

internal sealed class SkipDeliveryHandler(IUnitOfWork unitOfWork, IDeliveryStore store, IEventLog eventLog, IEventFactory events, IRequestContext request, TimeProvider time)
    : ICommandHandler<SkipDeliveryCommand, RetryDeliveriesResponse>
{
    public async Task<RetryDeliveriesResponse> HandleAsync(SkipDeliveryCommand command, CancellationToken cancellationToken)
    {
        var reason = command.Reason.Trim();
        new Validator().RequireText(reason, "Reason", 500).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var delivery = await store.GetForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Delivery {command.Id} not found.");
        if (delivery.Status is DeliveryStatuses.Delivered or DeliveryStatuses.Skipped)
        {
            throw new DomainRuleException($"That delivery is {delivery.Status} already.");
        }

        var envelope = events.Create(EventTypes.DeliverySkipped, "delivery", delivery.Id, new { delivery.Sink, delivery.EventId, delivery.EventType, delivery.Attempts }, reason);
        await store.ResolveAsync(delivery.Id, DeliveryStatuses.Skipped, request.ActorId, reason, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RetryDeliveriesResponse(0);
    }
}
