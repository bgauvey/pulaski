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
/// The site's maintenance requests (#127): those not closed when <paramref name="OpenOnly"/>, else those raised in the period (at most 92 days);
/// for one piece of equipment when given. How long each waited and its age are worked out now.
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
