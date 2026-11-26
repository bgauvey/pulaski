using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Security;
using Pulaski.Contracts.Alerts;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Alerts;
using Pulaski.Domain.Security;

namespace Pulaski.Application.Alerts;

/// <summary>Write side for alert subscriptions (#121): runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IAlertRepository
{
    /// <summary>The site of <paramref name="equipmentId"/> (itself, for a site); null when there is no such equipment.</summary>
    Task<Guid?> SiteOfAsync(Guid equipmentId, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid userId, Guid siteId, Guid? equipmentId, string kind, CancellationToken cancellationToken);

    Task<Versioned<AlertSubscription>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertAsync(AlertSubscription subscription, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(AlertSubscription subscription, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Records an alert as sent; false when it was already (another instance sent it).</summary>
    Task<bool> MarkSentAsync(Guid subscriptionId, Guid subjectId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side for alerts (#121).</summary>
public interface IAlertReadStore
{
    Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's subscriptions; <see cref="AlertSubscriptionDto.CanSee"/> is left for the handler.</summary>
    Task<IReadOnlyList<AlertSubscriptionDto>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// What active subscriptions of users with an e-mail address are due for, not yet sent: whatever reached its moment (a stop at its
    /// minutes, a call at its escalation) between <paramref name="since"/> (and not before the subscription was made) and <paramref name="now"/>.
    /// </summary>
    Task<IReadOnlyList<AlertDue>> ListDueAsync(DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Active subscriptions of <paramref name="kind"/> of active users with an e-mail address (#165).</summary>
    Task<IReadOnlyList<AlertSubscriber>> ListSubscribersAsync(string kind, CancellationToken cancellationToken);
}

/// <summary>Where alert e-mails can link to (#121).</summary>
public interface IAlertSettings
{
    /// <summary>The UI's public address, such as <c>https://mes.example.com</c>; null for messages without a link.</summary>
    string? UiBaseUrl { get; }
}

/// <summary>Alert e-mails without a link, until the host says where the UI is.</summary>
internal sealed class NoAlertLinks : IAlertSettings
{
    public string? UiBaseUrl => null;
}

/// <summary>
/// An alert due to one subscriber (#121): who, and what about. By kind: <see cref="What"/> is the stop's reason, the call's kind of help, the
/// chart's characteristic, the hold's reason or the short component; <see cref="Note"/> the call's note, the chart's material, the hold's
/// comment or the short job; <see cref="Start"/> and <see cref="End"/> the stop's times, or when it was raised, held or is needed;
/// <see cref="Item"/> the held lot or the quantity short, <see cref="ItemId"/> the held lot.
/// </summary>
public sealed record AlertDue(
    Guid SubscriptionId,
    string Kind,
    int? Minutes,
    Guid SubjectId,
    Guid UserId,
    string Issuer,
    string Subject,
    string Email,
    string UserName,
    Guid SiteId,
    string SiteCode,
    string? TimeZone,
    string? EquipmentCode,
    string? What,
    string? Note,
    DateTimeOffset Start,
    DateTimeOffset? End,
    string? Item = null,
    Guid? ItemId = null);

/// <summary>A subscription with its user, for alerts worked out in code rather than found in SQL (shortages, #165).</summary>
public sealed record AlertSubscriber(
    Guid SubscriptionId, string Kind, int? Minutes, Guid? EquipmentId, Guid UserId, string Issuer, string Subject, string Email, string UserName,
    Guid SiteId, string SiteCode, string? TimeZone);

/// <summary>Subscribe to an alert at a site, as the signed-in user (#121); they must be able to see what it is about there.</summary>
public sealed record CreateAlertSubscriptionCommand(UserAccess Caller, Guid SiteId, Guid? EquipmentId, string Kind, int? Minutes) : ICommand<CreatedResponse>;

/// <summary>Change a subscription's minutes, or switch it off or on; only the user's own.</summary>
public sealed record UpdateAlertSubscriptionCommand(UserAccess Caller, Guid Id, long ExpectedVersion, int? Minutes, bool IsActive) : ICommand<VersionResponse>;

/// <summary>The signed-in user's alerts.</summary>
public sealed record GetMyAlertsQuery(UserAccess Caller) : IQuery<MyAlertsDto>;

internal sealed class CreateAlertSubscriptionHandler(IUnitOfWork unitOfWork, IAlertRepository alerts, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<CreateAlertSubscriptionCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateAlertSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var subscription = AlertSubscription.Create(ids.NewId(), command.Caller.UserId, command.SiteId, command.EquipmentId, command.Kind, command.Minutes);
        var kind = AlertKind.Find(subscription.Kind)!;
        if (!command.Caller.Can(kind.ViewPermission, command.SiteId))
        {
            throw new ForbiddenException($"You can't see what this alert is about at that site ({kind.ViewPermission}).");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (await alerts.SiteOfAsync(command.SiteId, cancellationToken) != command.SiteId)
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        if (command.EquipmentId is { } equipmentId && await alerts.SiteOfAsync(equipmentId, cancellationToken) != command.SiteId)
        {
            throw new DomainRuleException("Choose a line or area of that site, or the whole site.");
        }

        if (await alerts.ExistsAsync(subscription.UserId, subscription.SiteId, subscription.EquipmentId, subscription.Kind, cancellationToken))
        {
            throw new ConflictException("You already have this alert there; it is listed above.");
        }

        var version = await alerts.InsertAsync(subscription, time.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(subscription.Id, version);
    }
}

internal sealed class UpdateAlertSubscriptionHandler(IUnitOfWork unitOfWork, IAlertRepository alerts, TimeProvider time)
    : ICommandHandler<UpdateAlertSubscriptionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateAlertSubscriptionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await alerts.GetForUpdateAsync(command.Id, cancellationToken);
        if (current is null || current.Value.UserId != command.Caller.UserId)
        {
            throw new NotFoundException($"Alert {command.Id} not found.");
        }

        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("This alert was changed elsewhere. Reload and try again.");
        }

        var after = current.Value.Edit(command.Minutes, command.IsActive);
        if (after == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var version = await alerts.UpdateAsync(after, time.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class GetMyAlertsHandler(IAlertReadStore store, IMailOutbox mail) : IQueryHandler<GetMyAlertsQuery, MyAlertsDto>
{
    public async Task<MyAlertsDto> HandleAsync(GetMyAlertsQuery query, CancellationToken cancellationToken)
    {
        var caller = query.Caller;
        var subscriptions = await store.ListAsync(caller.UserId, cancellationToken);
        return new MyAlertsDto
        {
            CanSend = mail.CanSend,
            Email = await store.GetEmailAsync(caller.UserId, cancellationToken),
            Kinds = [.. AlertKind.All.Where(k => k.IsBuilt).Select(k => new AlertKindDto(k.Code, k.Name, k.MinutesMeans, k.DefaultMinutes))],
            Subscriptions = [.. subscriptions.Select(s => s with
            {
                CanSee = AlertKind.Find(s.Kind) is { } kind && caller.Can(kind.ViewPermission, s.SiteId),
            })],
        };
    }
}

/// <summary>
/// Sends the alerts that are due (#121, D-109), one e-mail per subscriber and subject, recorded first so it goes once however many API
/// instances look. Only to users who may still see what it is about at the site; nothing without a mail server.
/// </summary>
public sealed class AlertSender(
    IUnitOfWork unitOfWork, IAlertRepository alerts, IAlertReadStore store, ISecurityReadStore security, IMailOutbox mail, IAlertSettings settings,
    IQueryHandler<Inventory.GetShortagesQuery, Contracts.Inventory.ShortageReportDto> shortages, TimeProvider timeProvider)
{
    /// <summary>How often shortages are worked out (#165): the check projects the schedule, so less often than the rest.</summary>
    public static readonly TimeSpan ShortageEvery = TimeSpan.FromMinutes(10);

    /// <summary>How far back a run looks: what reached its moment while the service was down (or arrived late) still goes, once.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    /// <param name="cancellationToken">Stops the run.</param>
    /// <param name="withShortages">Also work out shortages (<see cref="ShortageEvery"/>).</param>
    /// <returns>How many alerts were sent.</returns>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken, bool withShortages = true)
    {
        if (!mail.CanSend)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var due = (await store.ListDueAsync(now - Lookback, now, cancellationToken)).ToList();
        if (withShortages)
        {
            due.AddRange(await ShortagesDueAsync(now, cancellationToken));
        }

        if (due.Count == 0)
        {
            return 0;
        }

        // What each user may see now, asked once per user.
        var access = new Dictionary<Guid, UserAccess?>();
        var allowed = new List<AlertDue>();
        foreach (var alert in due)
        {
            if (!access.TryGetValue(alert.UserId, out var user))
            {
                access[alert.UserId] = user = await security.GetAccessAsync(alert.Issuer, alert.Subject, now, cancellationToken);
            }

            if (AlertKind.Find(alert.Kind) is { IsBuilt: true } kind && user?.Can(kind.ViewPermission, alert.SiteId) == true)
            {
                allowed.Add(alert);
            }
        }

        var messages = new List<(string To, AlertMessage Message)>();
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken))
        {
            foreach (var alert in allowed)
            {
                if (await alerts.MarkSentAsync(alert.SubscriptionId, alert.SubjectId, now, cancellationToken))
                {
                    messages.Add((alert.Email, AlertMessages.Compose(alert, now, settings.UiBaseUrl)));
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var (to, message) in messages)
        {
            mail.Enqueue(to, message.Subject, message.Body);
        }

        return messages.Count;
    }

    // A job short of a component within the subscription's minutes (#165): the site's shortage check, once per site and area or line asked
    // about, its lines not covered by stock or supply due in time. Once per job and component (a shortage that comes back isn't sent again).
    private async Task<List<AlertDue>> ShortagesDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = new List<AlertDue>();
        var subscribers = await store.ListSubscribersAsync(AlertKind.Shortage, cancellationToken);
        foreach (var scope in subscribers.GroupBy(s => (s.SiteId, s.EquipmentId)))
        {
            var horizonDays = (int)Math.Ceiling(scope.Max(s => s.Minutes ?? 0) / 1440.0) + 1;
            var report = await shortages.HandleAsync(new Inventory.GetShortagesQuery(scope.Key.SiteId, scope.Key.EquipmentId, Math.Min(horizonDays, 365)), cancellationToken);
            foreach (var subscriber in scope)
            {
                due.AddRange(AlertShortages.Due(subscriber, report, now));
            }
        }

        return due;
    }
}

public sealed record AlertMessage(string Subject, string Body);

/// <summary>What an alert e-mail says (#121). Pure.</summary>
public static class AlertMessages
{
    public static AlertMessage Compose(AlertDue alert, DateTimeOffset now, string? uiBaseUrl)
    {
        string Time(DateTimeOffset at) => SiteTime(at, alert.TimeZone).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string Minutes(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00} min" : $"{Math.Max(1, (int)span.TotalMinutes)} min";

        var (subject, lines, path) = alert.Kind switch
        {
            AlertKind.LongStop when alert.End is { } ended => (
                $"{alert.EquipmentCode} was stopped for {Minutes(ended - alert.Start)}",
                new[] { $"{alert.EquipmentCode} at {alert.SiteCode} was stopped from {Time(alert.Start)} to {Time(ended)}.", Reason(alert.What) },
                "machines"),
            AlertKind.LongStop => (
                $"{alert.EquipmentCode} has been stopped for {Minutes(now - alert.Start)}",
                new[] { $"{alert.EquipmentCode} at {alert.SiteCode} has been stopped since {Time(alert.Start)}.", Reason(alert.What) },
                "machines"),
            AlertKind.UncodedStop => (
                $"{alert.EquipmentCode}'s stop needs a reason",
                new[]
                {
                    $"{alert.EquipmentCode} at {alert.SiteCode} was stopped from {Time(alert.Start)} to {Time(alert.End ?? now)}, and nobody has said why.",
                    "Give it a reason on Machines, or at the line's station.",
                },
                "machines"),
            AlertKind.AndonEscalated => (
                $"{alert.What} call on {alert.EquipmentCode} is waiting",
                new[]
                {
                    $"{alert.EquipmentCode} at {alert.SiteCode} called for {alert.What} at {Time(alert.Start)}, and nobody has answered it for {Minutes(now - alert.Start)}.",
                    alert.Note is { } note ? $"Note: {note}" : null,
                },
                "andon"),
            AlertKind.SpcAlert => (
                $"SPC alert: {alert.What} on {alert.Note}{(alert.EquipmentCode is { } line ? $" ({line})" : "")}",
                new[]
                {
                    $"The control chart of {alert.What} for {alert.Note}{(alert.EquipmentCode is { } on ? $" on {on}" : "")} at {alert.SiteCode} signalled at {Time(alert.Start)}.",
                    "Find the cause, then close the alert with it and what was done.",
                },
                "quality/alerts"),
            AlertKind.LotHold => (
                $"Lot {alert.Item} put on hold",
                new[]
                {
                    $"Lot {alert.Item} of {alert.Note} at {alert.SiteCode}{(alert.EquipmentCode is { } at ? $", at {at}" : "")}, was put on hold at {Time(alert.Start)}.",
                    alert.What is { } why ? $"Reason: {why}." : null,
                },
                alert.ItemId is { } lot ? $"inventory/lots/{lot}" : "inventory/lots"),
            AlertKind.Shortage => (
                $"{alert.Note} will be short of {alert.What}",
                new[]
                {
                    $"Job {alert.Note} on {alert.EquipmentCode} at {alert.SiteCode} needs {alert.What} from {Time(alert.Start)}, and {alert.Item} of it isn't covered by stock or supply due by then.",
                },
                "inventory/shortages"),
            AlertKind.NcrActionOverdue => (
                $"NCR action overdue: {alert.What}",
                new[]
                {
                    $"An action of {alert.Note} at {alert.SiteCode} was due by {Time(alert.Start)} and isn't done: {alert.What}.",
                    alert.Item is { } owner ? $"Owner: {owner}." : "It has no owner.",
                },
                alert.ItemId is { } ncr ? $"quality/ncrs/{ncr}" : "quality/ncrs"),
            _ => throw new ArgumentOutOfRangeException(nameof(alert), alert.Kind, "Not an alert that is sent."),
        };

        var body = string.Join("\n", lines.Where(l => l is not null));
        if (uiBaseUrl is { Length: > 0 } baseUrl)
        {
            body += $"\n\n{baseUrl.TrimEnd('/')}/{path}";
        }

        body += $"\n\nYou get this because you subscribed to it in Pulaski ({alert.UserName}). Change or switch off your alerts under My alerts.";
        return new AlertMessage("[Pulaski] " + subject, body);
    }

    private static string? Reason(string? what) => what is null ? null : $"Reason: {what}.";

    private static DateTimeOffset SiteTime(DateTimeOffset at, string? timeZone)
    {
        try
        {
            return timeZone is null ? at : TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        }
        catch (TimeZoneNotFoundException)
        {
            return at;
        }
    }
}

/// <summary>Which shortages a subscriber is told about (#165). Pure.</summary>
public static class AlertShortages
{
    /// <summary>
    /// Each job line short of a component (not covered by stock or supply due by then) and needed within the subscriber's minutes from now;
    /// its subject is the job and component, so it goes once.
    /// </summary>
    public static IEnumerable<AlertDue> Due(AlertSubscriber subscriber, Contracts.Inventory.ShortageReportDto report, DateTimeOffset now)
    {
        var until = now.AddMinutes(subscriber.Minutes ?? 0);
        foreach (var material in report.Materials)
        {
            foreach (var line in material.Lines)
            {
                if (line.Uncovered > 0 && line.NeededFrom is { } needed && needed <= until)
                {
                    yield return new AlertDue(
                        subscriber.SubscriptionId, subscriber.Kind, subscriber.Minutes, SubjectOf(line.JobId, material.MaterialId), subscriber.UserId,
                        subscriber.Issuer, subscriber.Subject, subscriber.Email, subscriber.UserName, subscriber.SiteId, subscriber.SiteCode,
                        subscriber.TimeZone, line.LineCode, $"{material.MaterialCode} {material.MaterialName}", line.JobNumber, needed, null,
                        $"{line.Uncovered.ToString("#,0.###", System.Globalization.CultureInfo.InvariantCulture)} {material.BaseUomCode}");
                }
            }
        }
    }

    /// <summary>A stable id for a job's need of a component: the same job and component always give the same id.</summary>
    public static Guid SubjectOf(Guid jobId, Guid materialId)
    {
        Span<byte> bytes = stackalloc byte[32];
        jobId.TryWriteBytes(bytes[..16]);
        materialId.TryWriteBytes(bytes[16..]);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(bytes, hash);
        return new Guid(hash[..16]);
    }
}
