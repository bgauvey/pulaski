using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Maintenance;
using Pulaski.Domain;
using Pulaski.Domain.Maintenance;

namespace Pulaski.Application.Maintenance;

/// <summary>
/// Ask maintenance to look at a line or machine (#127, D-115), on its own or from one of its stops. While a request raised from the stop is
/// still open, asking again from it returns that request, with <see cref="MaintenanceRequestResponse.AlreadyOpen"/>, and records nothing.
/// </summary>
public sealed record RaiseMaintenanceRequestCommand(
    Guid EquipmentId, string? Priority, string? Description, Guid? DowntimeId = null, DateTimeOffset? DowntimeStartedAt = null, string? RaisedFrom = null)
    : ICommand<MaintenanceRequestResponse>;

/// <summary>Someone has started on the request.</summary>
public sealed record StartMaintenanceRequestCommand(Guid Id) : ICommand<MaintenanceRequestResponse>;

/// <summary>The request is done, with what was done; one nobody started is started by the same person at the same moment.</summary>
public sealed record CloseMaintenanceRequestCommand(Guid Id, string? Resolution) : ICommand<MaintenanceRequestResponse>;

internal sealed class RaiseMaintenanceRequestHandler(
    IUnitOfWork unitOfWork, IMaintenanceRepository maintenance, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<RaiseMaintenanceRequestCommand, MaintenanceRequestResponse>
{
    /// <summary>Equipment a request can be for: below an area (a line, a machine, a unit, a feed point).</summary>
    private static readonly string[] Levels = ["work_center", "work_unit", "equipment_module"];

    public async Task<MaintenanceRequestResponse> HandleAsync(RaiseMaintenanceRequestCommand command, CancellationToken cancellationToken)
    {
        if ((command.DowntimeId is null) != (command.DowntimeStartedAt is null))
        {
            throw ValidationException.Field("DowntimeStartedAt", "A stop is named by its id and its start.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        MaintenanceStop? stop = null;
        if (command.DowntimeId is { } downtimeId)
        {
            stop = await maintenance.GetStopAsync(downtimeId, command.DowntimeStartedAt!.Value, cancellationToken)
                ?? throw new NotFoundException("That stop wasn't found, or was replaced by an adjustment.");
        }

        // The equipment first, so two requests from one of its stops serialize and only one stays open.
        var equipmentId = stop?.EquipmentId ?? command.EquipmentId;
        var equipment = await maintenance.LockEquipmentAsync(equipmentId, cancellationToken)
            ?? throw new NotFoundException($"Equipment {equipmentId} not found.");
        if (!Levels.Contains(equipment.Level) || equipment.SiteId is not { } siteId)
        {
            throw new DomainRuleException($"{equipment.Code} isn't a line or machine; request maintenance for one.");
        }

        if (!equipment.IsActive)
        {
            throw new DomainRuleException($"{equipment.Code} is switched off; request maintenance for equipment in use.");
        }

        if (stop is not null && await maintenance.FindOpenForStopForUpdateAsync(stop.Id, cancellationToken) is { } open)
        {
            return new MaintenanceRequestResponse(open.Id, open.Number, open.Status, AlreadyOpen: true);
        }

        var number = MaintenanceRequest.NumberFor(await maintenance.NextNumberAsync(cancellationToken));
        var envelope = events.Create(
            EventTypes.MaintenanceRequestRaised, "maintenance_request", ids.NewId(),
            new { number, Equipment = equipment.Code, command.Priority, command.Description, Stop = stop is null ? null : new { stop.Id, stop.StartedAt }, command.RaisedFrom });
        var request = MaintenanceRequest.Raise(
            envelope.Subject, siteId, equipment.Id, number, command.Priority, command.Description, envelope.OccurredAt, envelope.ActorId, command.RaisedFrom,
            stop?.Id, stop?.StartedAt);
        await maintenance.InsertAsync(request, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MaintenanceRequestResponse(request.Id, request.Number, request.Status);
    }
}

internal sealed class StartMaintenanceRequestHandler(IUnitOfWork unitOfWork, IMaintenanceRepository maintenance, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<StartMaintenanceRequestCommand, MaintenanceRequestResponse>
{
    public async Task<MaintenanceRequestResponse> HandleAsync(StartMaintenanceRequestCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var request = await maintenance.GetForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Maintenance request {command.Id} not found.");
        var envelope = events.Create(EventTypes.MaintenanceRequestStarted, "maintenance_request", request.Id, new { request.Number, Before = new { request.Status } });
        var after = request.Start(envelope.OccurredAt, envelope.ActorId);
        await maintenance.UpdateAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MaintenanceRequestResponse(after.Id, after.Number, after.Status);
    }
}

internal sealed class CloseMaintenanceRequestHandler(IUnitOfWork unitOfWork, IMaintenanceRepository maintenance, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<CloseMaintenanceRequestCommand, MaintenanceRequestResponse>
{
    public async Task<MaintenanceRequestResponse> HandleAsync(CloseMaintenanceRequestCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var request = await maintenance.GetForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Maintenance request {command.Id} not found.");
        var envelope = events.Create(
            EventTypes.MaintenanceRequestClosed, "maintenance_request", request.Id, new { request.Number, Before = new { request.Status }, command.Resolution });
        var after = request.Close(envelope.OccurredAt, envelope.ActorId, command.Resolution);
        await maintenance.UpdateAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MaintenanceRequestResponse(after.Id, after.Number, after.Status);
    }
}

/// <summary>
/// A CMMS's answer about a request (#127c, D-115), through feed <paramref name="Feed"/>: <c>accepted</c> records its number for the request,
/// <c>started</c> and <c>closed</c> work it as a person would (the actor is <c>receiver:{feed}</c>). Answers repeat safely: starting one already
/// started, or closing one already closed, changes nothing.
/// </summary>
public sealed record UpdateMaintenanceFromCmmsCommand(string Feed, string Number, string Action, string? Reference, string? Resolution)
    : ICommand<MaintenanceRequestResponse>;

/// <summary>What a CMMS may say about a request (#127c).</summary>
public static class CmmsActions
{
    public const string Accepted = "accepted";
    public const string Started = "started";
    public const string Closed = "closed";

    public static IReadOnlyList<string> All { get; } = [Accepted, Started, Closed];
}

internal sealed class UpdateMaintenanceFromCmmsHandler(
    IUnitOfWork unitOfWork, IMaintenanceRepository maintenance, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<UpdateMaintenanceFromCmmsCommand, MaintenanceRequestResponse>
{
    public async Task<MaintenanceRequestResponse> HandleAsync(UpdateMaintenanceFromCmmsCommand command, CancellationToken cancellationToken)
    {
        var action = (command.Action ?? "").Trim().ToLowerInvariant();
        var number = (command.Number ?? "").Trim().ToUpperInvariant();
        new Validator()
            .Require(number.Length > 0, nameof(command.Number), "Required.")
            .Require(CmmsActions.All.Contains(action), nameof(command.Action), "accepted, started or closed.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var request = await maintenance.FindByNumberForUpdateAsync(number, cancellationToken)
            ?? throw new NotFoundException($"There is no maintenance request {number}.");

        // A CMMS sends again when it isn't sure it was heard: what is done already stays done.
        if ((action == CmmsActions.Started && request.Status != MaintenanceStatuses.Open) || (action == CmmsActions.Closed && request.IsClosed))
        {
            return new MaintenanceRequestResponse(request.Id, request.Number, request.Status);
        }

        var envelope = events.Create(
            action switch
            {
                CmmsActions.Accepted => EventTypes.MaintenanceRequestLinked,
                CmmsActions.Started => EventTypes.MaintenanceRequestStarted,
                _ => EventTypes.MaintenanceRequestClosed,
            },
            "maintenance_request", request.Id,
            new { request.Number, Before = new { request.Status, request.ExternalReference }, command.Feed, Action = action, command.Reference, command.Resolution });

        // Started and closed take the event's moment and actor (receiver:{feed}), as when a person does it.
        var after = action switch
        {
            CmmsActions.Accepted => request.LinkExternal(command.Feed, command.Reference),
            CmmsActions.Started => request.Start(envelope.OccurredAt, envelope.ActorId),
            _ => request.Close(envelope.OccurredAt, envelope.ActorId, command.Resolution),
        };
        if (after == request)
        {
            return new MaintenanceRequestResponse(request.Id, request.Number, request.Status);
        }

        await maintenance.UpdateAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MaintenanceRequestResponse(after.Id, after.Number, after.Status);
    }
}

/// <summary>
/// The site's maintenance requests (#127): those not closed when <paramref name="OpenOnly"/>, else those raised in the period (at most 92 days);
/// for a piece of equipment and what is below it when given (a line's requests include its machines'). How long each waited and its age are
/// worked out now.
/// </summary>
public sealed record ListMaintenanceRequestsQuery(Guid SiteId, bool OpenOnly, DateTimeOffset? From = null, DateTimeOffset? To = null, Guid? EquipmentId = null)
    : IQuery<IReadOnlyList<MaintenanceRequestDto>>;

internal sealed class ListMaintenanceRequestsHandler(IMaintenanceReadStore store, TimeProvider time)
    : IQueryHandler<ListMaintenanceRequestsQuery, IReadOnlyList<MaintenanceRequestDto>>
{
    public static readonly TimeSpan LongestPeriod = TimeSpan.FromDays(92);

    public async Task<IReadOnlyList<MaintenanceRequestDto>> HandleAsync(ListMaintenanceRequestsQuery query, CancellationToken cancellationToken)
    {
        if (!query.OpenOnly && query.From is { } from && query.To is { } to && (to <= from || to - from > LongestPeriod))
        {
            throw new DomainRuleException($"Choose a period of at most {LongestPeriod.TotalDays:0} days.");
        }

        var now = time.GetUtcNow();
        var requests = await store.ListAsync(query.SiteId, query.OpenOnly, query.From, query.To, query.EquipmentId, cancellationToken);
        return [.. requests.Select(r => MaintenanceView.Figure(r, now))];
    }
}

/// <summary>The order requests are worked in (#127): waiting before in progress, high priority first, then the longest waiting. Pure.</summary>
public static class MaintenanceOrder
{
    public static IEnumerable<MaintenanceRequestDto> Waiting(IEnumerable<MaintenanceRequestDto> requests) => requests
        .OrderBy(r => r.Status == MaintenanceStatuses.Open ? 0 : 1)
        .ThenBy(r => r.Priority switch { MaintenancePriorities.High => 0, MaintenancePriorities.Normal => 1, _ => 2 })
        .ThenBy(r => r.RaisedAt);
}

/// <summary>A request's waiting time and age as of now (#127). Pure.</summary>
public static class MaintenanceView
{
    public static MaintenanceRequestDto Figure(MaintenanceRequestDto request, DateTimeOffset now)
    {
        var domain = new MaintenanceRequest(
            request.Id, request.SiteId, request.EquipmentId, request.Number, request.Status, request.Priority, request.Description, request.RaisedAt,
            request.RaisedBy, request.RaisedFrom, request.DowntimeId, request.DowntimeStartedAt, request.StartedAt, request.StartedBy, request.ClosedAt,
            request.ClosedBy, request.Resolution);
        return request with
        {
            WaitedSeconds = (int)Math.Max(0, MaintenanceRules.Waited(domain, now).TotalSeconds),
            AgeSeconds = (int)Math.Max(0, MaintenanceRules.Age(domain, now).TotalSeconds),
        };
    }
}
