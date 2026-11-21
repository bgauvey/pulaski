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
/// An alert due to one subscriber (#121): who, and what about. <see cref="What"/> is the stop's reason or the call's kind of help,
/// <see cref="Note"/> the call's note; <see cref="Start"/> and <see cref="End"/> the stop's times, or the call's raising.
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
    string EquipmentCode,
    string? What,
    string? Note,
    DateTimeOffset Start,
    DateTimeOffset? End);

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
    TimeProvider timeProvider)
{
    /// <summary>How far back a run looks: what reached its moment while the service was down (or arrived late) still goes, once.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    /// <returns>How many alerts were sent.</returns>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        if (!mail.CanSend)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var due = await store.ListDueAsync(now - Lookback, now, cancellationToken);
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
            _ => throw new ArgumentOutOfRangeException(nameof(alert), alert.Kind, "Not an alert that is sent yet."),
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
