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
public sealed record DeliveryTarget(string Sink, bool Enabled, string? TargetHost, IReadOnlyList<string> EventTypes)
{
    /// <summary>The feed's key in routes: <c>erp</c> or <c>documents</c> (#78).</summary>
    public string Feed { get; init; } = "erp";

    /// <summary>What it is for, in messages: "ERP", "document system".</summary>
    public string Label { get; init; } = "ERP";

    /// <summary>A certificate's stored file goes with its event (#78).</summary>
    public bool IncludesFiles { get; init; }

    /// <summary>The receiver's full address: only for the settings screen, which needs it to change it (#73).</summary>
    public string? Url { get; init; }
    public int MaxAttempts { get; init; } = DeliveryPolicy.DefaultMaxAttempts;
    public int TimeoutSeconds { get; init; } = 15;
    public int PollSeconds { get; init; } = 5;
    public bool StartFromNow { get; init; } = true;

    /// <summary>The secret is set in the API's configuration; without it nothing is sent.</summary>
    public bool SecretConfigured { get; init; }
}

/// <summary>A feed's settings as saved from the screen (#73), with their version and who saved them.</summary>
public sealed record StoredFeedSettings(DeliveryFeedSettings Settings, long Version, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>A feed's saved settings (#73). Reads on their own connection; saving runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IDeliveryFeedStore
{
    /// <summary>The saved settings; null when the feed has never been saved and runs on its configuration.</summary>
    Task<StoredFeedSettings?> GetAsync(string feed, CancellationToken cancellationToken);

    /// <summary>The saved settings locked; the row is made first when there is none, and then its version is 0.</summary>
    Task<StoredFeedSettings> GetForUpdateAsync(string feed, DeliveryFeedSettings defaults, CancellationToken cancellationToken);

    /// <returns>The new version.</returns>
    Task<long> SaveAsync(DeliveryFeedSettings settings, string actor, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Operators' side of outbound deliveries (D-061); runs inside the current <see cref="IUnitOfWork"/> for changes.</summary>
public interface IDeliveryStore
{
    Task<IReadOnlyList<DeliveryDto>> ListAsync(string sink, string? status, int limit, CancellationToken cancellationToken);

    Task<DeliveryCounts> CountAsync(string sink, DateTimeOffset now, CancellationToken cancellationToken);

    Task<DeliveryRecord?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Sets a delivery's status, due now, with who resolved it and why.</summary>
    Task ResolveAsync(Guid id, string status, string actor, string? note, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The sink's delivery of the event, locked; null when it never captured it.</summary>
    Task<DeliveryRecord?> GetByEventForUpdateAsync(string sink, Guid eventId, CancellationToken cancellationToken);

    /// <summary>Records a receiver's acknowledgement (#73); with <paramref name="status"/>, also sets the status and, for a failure, why (<paramref name="failure"/>).</summary>
    Task AcknowledgeAsync(Guid id, Acknowledgement acknowledgement, string source, string? status, string? failure, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every failed delivery of the sink back to pending, due now; returns how many.</summary>
    Task<int> RetryFailedAsync(string sink, string actor, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Where deliveries go and which events (D-061), from the API's configuration.</summary>
public interface IDeliveryTargets
{
    DeliveryTarget Erp { get; }

    /// <summary>A feed by key (<c>erp</c>, <c>documents</c>); null for one that doesn't exist.</summary>
    DeliveryTarget? Find(string feed);
}

public static class DeliveryFeeds
{
    public static DeliveryTarget Target(IDeliveryTargets targets, string? feed) =>
        targets.Find(feed ?? "erp") ?? throw new NotFoundException($"There is no feed '{feed}'.");

    /// <summary>
    /// The feed as it runs (#73): its saved settings over its configuration, which keeps the secret. A feed never saved runs on its
    /// configuration alone.
    /// </summary>
    public static DeliveryTarget Effective(DeliveryTarget configured, DeliveryFeedSettings? saved)
    {
        if (saved is null)
        {
            return configured;
        }

        var host = Uri.TryCreate(saved.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
        return configured with
        {
            Enabled = saved.Enabled && host is not null,
            TargetHost = host,
            EventTypes = DeliveryKinds.PrefixesOf(saved.EventKinds),
            IncludesFiles = saved.IncludeFiles,
            Url = saved.Url,
            MaxAttempts = saved.MaxAttempts,
            TimeoutSeconds = saved.TimeoutSeconds,
            PollSeconds = saved.PollSeconds,
            StartFromNow = saved.StartFromNow,
        };
    }

    public static async Task<DeliveryTarget> EffectiveAsync(IDeliveryTargets targets, IDeliveryFeedStore? feeds, string? feed, CancellationToken cancellationToken)
    {
        var configured = Target(targets, feed);
        return feeds is null ? configured : Effective(configured, (await feeds.GetAsync(configured.Feed, cancellationToken))?.Settings);
    }

    /// <summary>The configuration's values as settings, the starting point before the screen first saves.</summary>
    public static DeliveryFeedSettings FromConfiguration(DeliveryTarget configured) => new(
        configured.Feed, configured.Enabled, configured.Url, DeliveryKinds.KindsOf(configured.EventTypes), configured.IncludesFiles, configured.MaxAttempts,
        configured.TimeoutSeconds, configured.PollSeconds, configured.StartFromNow);
}

public sealed record GetDeliveryStatusQuery(string Feed = "erp") : IQuery<DeliveryStatusDto>;

public sealed record ListDeliveriesQuery(string? Status = null, int Limit = 200, string Feed = "erp") : IQuery<IReadOnlyList<DeliveryDto>>;

/// <summary>Tries a failed delivery again now; its attempts carry on counting.</summary>
public sealed record RetryDeliveryCommand(Guid Id) : ICommand<RetryDeliveriesResponse>;

/// <summary>Tries every failed delivery of the feed again now.</summary>
public sealed record RetryFailedDeliveriesCommand(string Feed = "erp") : ICommand<RetryDeliveriesResponse>;

/// <summary>
/// Gives up on a delivery, with a reason (D-061): someone has put it right in the ERP by hand, or it should never go. It stops holding
/// back the subject's later events. The event itself is untouched.
/// </summary>
public sealed record SkipDeliveryCommand(Guid Id, string Reason) : ICommand<RetryDeliveriesResponse>;

internal sealed class GetDeliveryStatusHandler(IDeliveryStore store, IDeliveryTargets targets, TimeProvider time, IDeliveryFeedStore? feeds = null)
    : IQueryHandler<GetDeliveryStatusQuery, DeliveryStatusDto>
{
    public async Task<DeliveryStatusDto> HandleAsync(GetDeliveryStatusQuery query, CancellationToken cancellationToken)
    {
        var target = await DeliveryFeeds.EffectiveAsync(targets, feeds, query.Feed, cancellationToken);
        var counts = await store.CountAsync(target.Sink, time.GetUtcNow(), cancellationToken);
        return new DeliveryStatusDto
        {
            Sink = target.Sink,
            Feed = target.Feed,
            Label = target.Label,
            IncludesFiles = target.IncludesFiles,
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
        return store.ListAsync(DeliveryFeeds.Target(targets, query.Feed).Sink, query.Status, query.Limit, cancellationToken);
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
        var target = DeliveryFeeds.Target(targets, command.Feed);
        var retried = await store.RetryFailedAsync(target.Sink, request.ActorId, time.GetUtcNow(), cancellationToken);
        if (retried > 0)
        {
            var envelope = events.Create(EventTypes.DeliveryRetried, "delivery", Guid.Empty, new { target.Sink, Retried = retried, All = true });
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
