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
    int TimeoutSeconds, int PollSeconds, bool StartFromNow, string? Name = null, string? Topic = null) : ICommand<VersionResponse>;

/// <summary>Adds a feed (#74): a code (fixed), a name and a kind (webhook or MQTT, fixed). It starts switched off, sending inventory movements.</summary>
public sealed record CreateDeliveryFeedCommand(string Code, string Name, string Kind) : ICommand<VersionResponse>;

/// <summary>Every feed, built-in and added, for the Integrations screen's tabs (#74).</summary>
public sealed record ListDeliveryFeedsQuery : IQuery<IReadOnlyList<DeliveryFeedSummaryDto>>;

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
        if (saved is null && !configured.IsBuiltIn)
        {
            throw new NotFoundException($"There is no feed '{query.Feed}'.");
        }

        var settings = saved?.Settings ?? DeliveryFeeds.FromConfiguration(configured);
        var covered = DeliveryKinds.PrefixesOf(settings.EventKinds);
        return new DeliveryFeedSettingsDto
        {
            Feed = configured.Feed,
            Label = settings.Name.Length > 0 ? settings.Name : configured.Label,
            Kind = settings.Kind,
            Topic = settings.Topic,
            IsBuiltIn = configured.IsBuiltIn,
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
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!configured.IsBuiltIn && await feeds.GetAsync(configured.Feed, cancellationToken) is null)
        {
            throw new NotFoundException($"There is no feed '{command.Feed}'.");
        }

        var current = await feeds.GetForUpdateAsync(configured.Feed, DeliveryFeeds.FromConfiguration(configured), cancellationToken);

        // The code and kind never change: the sink, and so what is queued, depends on them.
        var after = current.Settings with
        {
            Enabled = command.Enabled,
            Url = string.IsNullOrWhiteSpace(command.Url) ? null : command.Url.Trim(),
            EventKinds = [.. command.EventKinds.Distinct()],
            IncludeFiles = command.IncludeFiles,
            MaxAttempts = command.MaxAttempts,
            TimeoutSeconds = command.TimeoutSeconds,
            PollSeconds = command.PollSeconds,
            StartFromNow = command.StartFromNow,
            Name = command.Name?.Trim() is { Length: > 0 } name ? name : current.Settings.Name,
            Topic = current.Settings.Kind == DeliveryFeedKinds.Mqtt ? command.Topic?.Trim() : null,
        };
        if (after.Problems() is { Count: > 0 } problems)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Settings"] = [.. problems] });
        }

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
    IUnitOfWork unitOfWork, IDeliveryTargets targets, IDeliveryFeedStore feeds, IDeliveryStore store, IEventLog eventLog, IEventFactory events, TimeProvider time)
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
        var target = await DeliveryFeeds.ResolveAsync(targets, feeds, command.Feed, cancellationToken);
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

internal sealed class CreateDeliveryFeedHandler(
    IUnitOfWork unitOfWork, IDeliveryTargets targets, IDeliveryFeedStore feeds, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<CreateDeliveryFeedCommand, VersionResponse>
{
    public const string DefaultTopic = "pulaski/{type}";

    public async Task<VersionResponse> HandleAsync(CreateDeliveryFeedCommand command, CancellationToken cancellationToken)
    {
        var code = (command.Code ?? "").Trim().ToLowerInvariant();
        var kind = (command.Kind ?? "").Trim().ToLowerInvariant();
        new Validator()
            .Require(DeliveryFeedKinds.CodeProblem(code) is null, nameof(command.Code), DeliveryFeedKinds.CodeProblem(code) ?? "")
            .Require(!DeliveryFeedKinds.BuiltIn.Contains(code), nameof(command.Code), "That code belongs to a built-in feed.")
            .Require(DeliveryFeedKinds.All.Contains(kind), nameof(command.Kind), "webhook or mqtt.")
            .RequireText((command.Name ?? "").Trim(), nameof(command.Name), DeliveryFeedSettings.MaxName)
            .ThrowIfInvalid();
        var configured = DeliveryFeeds.Target(targets, code);
        var feed = new DeliveryFeedSettings(
            code, false, null, [DeliveryKinds.Inventory], false, DeliveryPolicy.DefaultMaxAttempts, 15, 5, true, command.Name!.Trim(), kind,
            kind == DeliveryFeedKinds.Mqtt ? DefaultTopic : null);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var envelope = events.Create(EventTypes.DeliveryFeedCreated, "delivery_feed", Guid.Empty, new { Feed = code, feed.Name, feed.Kind, SecretConfigured = configured.SecretConfigured });
        if (!await feeds.InsertAsync(feed, envelope.ActorId, time.GetUtcNow(), cancellationToken))
        {
            throw new ConflictException($"There is already a feed '{code}'.");
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

internal sealed class ListDeliveryFeedsHandler(IDeliveryTargets targets, IDeliveryFeedStore feeds) : IQueryHandler<ListDeliveryFeedsQuery, IReadOnlyList<DeliveryFeedSummaryDto>>
{
    public async Task<IReadOnlyList<DeliveryFeedSummaryDto>> HandleAsync(ListDeliveryFeedsQuery query, CancellationToken cancellationToken)
    {
        var saved = (await feeds.ListAsync(cancellationToken)).ToDictionary(f => f.Settings.Feed);
        var codes = DeliveryFeedKinds.BuiltIn.Concat(saved.Keys.Where(k => !DeliveryFeedKinds.BuiltIn.Contains(k)).Order(StringComparer.Ordinal));
        return [.. codes.Select(code =>
        {
            var target = DeliveryFeeds.Effective(DeliveryFeeds.Target(targets, code), saved.GetValueOrDefault(code)?.Settings);
            return new DeliveryFeedSummaryDto(code, target.Label, target.Kind, target.IsBuiltIn, target.Enabled, target.TargetHost);
        })];
    }
}
