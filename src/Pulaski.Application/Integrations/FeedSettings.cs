using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Integrations;
using Pulaski.Domain.Integrations;

namespace Pulaski.Application.Integrations;

/// <summary>A feed's settings for the screen (#73): saved, or else its configuration as the starting point.</summary>
public sealed record GetDeliveryFeedSettingsQuery(string Feed) : IQuery<DeliveryFeedSettingsDto>;

/// <summary>Saves a feed's settings (#73): everything but the secret. The relay picks them up on its next round; no restart.</summary>
public sealed record SaveDeliveryFeedSettingsCommand(
    string Feed, long ExpectedVersion, bool Enabled, string? Url, IReadOnlyList<string> EventKinds, bool IncludeFiles, int MaxAttempts,
    int TimeoutSeconds, int PollSeconds, bool StartFromNow) : ICommand<VersionResponse>;

/// <summary>
/// Records a receiver's acknowledgement of one event (#73), from the signed callback. Rejected, the delivery fails and holds its subject's
/// later events until retried or skipped; accepted, a delivery that had failed is delivered after all.
/// </summary>
public sealed record AcknowledgeDeliveryCommand(string Feed, Guid EventId, string Status, string? Reference, string? Message) : ICommand<AcknowledgementResponse>;

internal sealed class GetDeliveryFeedSettingsHandler(IDeliveryTargets targets, IDeliveryFeedStore feeds) : IQueryHandler<GetDeliveryFeedSettingsQuery, DeliveryFeedSettingsDto>
{
    public async Task<DeliveryFeedSettingsDto> HandleAsync(GetDeliveryFeedSettingsQuery query, CancellationToken cancellationToken)
    {
        var configured = DeliveryFeeds.Target(targets, query.Feed);
        var saved = await feeds.GetAsync(configured.Feed, cancellationToken);
        var settings = saved?.Settings ?? DeliveryFeeds.FromConfiguration(configured);
        var covered = DeliveryKinds.PrefixesOf(settings.EventKinds);
        return new DeliveryFeedSettingsDto
        {
            Feed = configured.Feed,
            Label = configured.Label,
            IsSaved = saved is not null,
            Enabled = settings.Enabled,
            Url = settings.Url,
            EventKinds = settings.EventKinds,
            OtherEventTypes = saved is null ? [.. configured.EventTypes.Except(covered)] : [],
            IncludeFiles = settings.IncludeFiles,
            MaxAttempts = settings.MaxAttempts,
            TimeoutSeconds = settings.TimeoutSeconds,
            PollSeconds = settings.PollSeconds,
            StartFromNow = settings.StartFromNow,
            SecretConfigured = configured.SecretConfigured,
            AcknowledgementPath = $"/api/integrations/{configured.Feed}/acknowledgements",
            Kinds = [.. DeliveryKinds.All.Select(k => new DeliveryKindDto(k.Code, k.Label, k.Prefixes))],
            Version = saved?.Version ?? 0,
            UpdatedAt = saved?.UpdatedAt,
            UpdatedBy = saved?.UpdatedBy,
        };
    }
}

internal sealed class SaveDeliveryFeedSettingsHandler(
    IUnitOfWork unitOfWork, IDeliveryTargets targets, IDeliveryFeedStore feeds, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<SaveDeliveryFeedSettingsCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SaveDeliveryFeedSettingsCommand command, CancellationToken cancellationToken)
    {
        var configured = DeliveryFeeds.Target(targets, command.Feed);
        var after = new DeliveryFeedSettings(
            configured.Feed, command.Enabled, string.IsNullOrWhiteSpace(command.Url) ? null : command.Url.Trim(), [.. command.EventKinds.Distinct()],
            command.IncludeFiles, command.MaxAttempts, command.TimeoutSeconds, command.PollSeconds, command.StartFromNow);
        if (after.Problems() is { Count: > 0 } problems)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Settings"] = [.. problems] });
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await feeds.GetForUpdateAsync(configured.Feed, DeliveryFeeds.FromConfiguration(configured), cancellationToken);
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"The {configured.Label} feed's settings were changed by someone else. Reload and try again.");
        }

        // Audited with before and after; the address is shown whole only here, to those who may change it, and the secret never.
        var envelope = events.Create(EventTypes.DeliveryFeedSettingsSaved, "delivery_feed", Guid.Empty, new { configured.Feed, Before = current.Settings, After = after });
        var version = await feeds.SaveAsync(after, envelope.ActorId, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class AcknowledgeDeliveryHandler(
    IUnitOfWork unitOfWork, IDeliveryTargets targets, IDeliveryStore store, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<AcknowledgeDeliveryCommand, AcknowledgementResponse>
{
    public async Task<AcknowledgementResponse> HandleAsync(AcknowledgeDeliveryCommand command, CancellationToken cancellationToken)
    {
        var status = command.Status?.Trim().ToLowerInvariant() ?? "";
        new Validator()
            .Require(command.EventId != Guid.Empty, nameof(command.EventId), "Required.")
            .Require(AcknowledgementStatuses.All.Contains(status), nameof(command.Status), "accepted or rejected.")
            .ThrowIfInvalid();
        var acknowledgement = new Acknowledgement(status, Acknowledgement.Trim(command.Reference), Acknowledgement.Trim(command.Message));
        var target = DeliveryFeeds.Target(targets, command.Feed);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var delivery = await store.GetByEventForUpdateAsync(target.Sink, command.EventId, cancellationToken)
            ?? throw new NotFoundException($"The {target.Label} feed has no delivery of event {command.EventId}.");

        // A rejection stops the subject as a refused delivery does; an acceptance clears a failure. A skipped delivery only records it.
        var (newStatus, error) = (acknowledgement.IsRejected, delivery.Status) switch
        {
            (true, DeliveryStatuses.Delivered or DeliveryStatuses.Pending) => (DeliveryStatuses.Failed, acknowledgement.RejectionText),
            (false, DeliveryStatuses.Failed or DeliveryStatuses.Pending) => (DeliveryStatuses.Delivered, (string?)null),
            _ => ((string?)null, (string?)null),
        };
        var envelope = events.Create(
            EventTypes.DeliveryAcknowledged, "delivery", delivery.Id,
            new { delivery.Sink, delivery.EventId, delivery.EventType, acknowledgement.Status, acknowledgement.Reference, acknowledgement.Message, Before = delivery.Status, After = newStatus ?? delivery.Status });
        await store.AcknowledgeAsync(delivery.Id, acknowledgement, AcknowledgementSources.Callback, newStatus, error, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcknowledgementResponse(delivery.Id, newStatus ?? delivery.Status);
    }
}
