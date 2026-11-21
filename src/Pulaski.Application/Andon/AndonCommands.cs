using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Andon;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Andon;

namespace Pulaski.Application.Andon;

/// <summary>A kind of help a line can call for at a site (#125, D-108).</summary>
public sealed record CreateAndonTypeCommand(Guid SiteId, string Code, string Name, int EscalateAfterMinutes, int SortOrder) : ICommand<CreatedResponse>;

/// <summary>A call type's name, escalation time, order and whether it is offered; its code never changes.</summary>
public sealed record UpdateAndonTypeCommand(Guid Id, long ExpectedVersion, string Name, int EscalateAfterMinutes, int SortOrder, bool IsActive)
    : ICommand<VersionResponse>;

/// <summary>
/// Call for help from a line (#125, D-108). While a call of the type is still open on the line, calling again is the same call: it is
/// returned, with <see cref="AndonCallResponse.AlreadyOpen"/>, and nothing is recorded.
/// </summary>
public sealed record RaiseAndonCallCommand(Guid LineId, Guid TypeId, string? Note, string? RaisedFrom = null) : ICommand<AndonCallResponse>;

/// <summary>Whoever answers a call has come: it stops waiting.</summary>
public sealed record AcknowledgeAndonCallCommand(Guid Id) : ICommand<AndonCallResponse>;

/// <summary>The call is done, with what was done; one nobody acknowledged is acknowledged by the same person at the same moment.</summary>
public sealed record ResolveAndonCallCommand(Guid Id, string? Resolution) : ICommand<AndonCallResponse>;

internal sealed class CreateAndonTypeHandler(
    IUnitOfWork unitOfWork, IAndonRepository andon, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<CreateAndonTypeCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateAndonTypeCommand command, CancellationToken cancellationToken)
    {
        var type = AndonType.Create(ids.NewId(), command.SiteId, command.Code, command.Name, command.EscalateAfterMinutes, command.SortOrder);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await andon.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        if (await andon.TypeCodeExistsAsync(command.SiteId, type.Code, cancellationToken))
        {
            throw new ConflictException($"There is already a call type {type.Code} at this site.");
        }

        var envelope = events.Create(EventTypes.AndonTypeCreated, "andon_type", type.Id, new { After = type });
        var version = await andon.InsertTypeAsync(type, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(type.Id, version);
    }
}

internal sealed class UpdateAndonTypeHandler(IUnitOfWork unitOfWork, IAndonRepository andon, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<UpdateAndonTypeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateAndonTypeCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await andon.GetTypeForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Call type {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Name} was changed by someone else. Reload and try again.");
        }

        var after = current.Value.Edit(command.Name, command.EscalateAfterMinutes, command.SortOrder, command.IsActive);
        if (after == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.AndonTypeChanged, "andon_type", after.Id, new { Before = current.Value, After = after });
        var version = await andon.UpdateTypeAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class RaiseAndonCallHandler(
    IUnitOfWork unitOfWork, IAndonRepository andon, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<RaiseAndonCallCommand, AndonCallResponse>
{
    public async Task<AndonCallResponse> HandleAsync(RaiseAndonCallCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // The line first, so two calls of a type on it serialize and only one stays open.
        var line = await andon.LockLineAsync(command.LineId, cancellationToken) ?? throw new NotFoundException($"Line {command.LineId} not found.");
        if (!line.CanRunJob || !line.IsActive)
        {
            throw new DomainRuleException($"{line.Code} {(line.IsActive ? "isn't a line" : "is switched off")}; call for help from a line.");
        }

        var type = (await andon.GetTypeForUpdateAsync(command.TypeId, cancellationToken))?.Value;
        if (type is null || type.SiteId != line.SiteId)
        {
            throw new NotFoundException($"There is no such call type at {line.Code}'s site.");
        }

        if (await andon.FindOpenCallForUpdateAsync(line.Id, type.Id, cancellationToken) is { } open)
        {
            return new AndonCallResponse(open.Id, open.Status, AlreadyOpen: true);
        }

        var envelope = events.Create(EventTypes.AndonCallRaised, "andon_call", ids.NewId(), new { line.Code, Type = type.Code, command.Note, command.RaisedFrom });
        var call = AndonCall.Raise(envelope.Subject, line.SiteId, line.Id, type, command.Note, envelope.OccurredAt, envelope.ActorId, command.RaisedFrom);
        await andon.InsertCallAsync(call, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AndonCallResponse(call.Id, call.Status);
    }
}

internal sealed class AcknowledgeAndonCallHandler(IUnitOfWork unitOfWork, IAndonRepository andon, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<AcknowledgeAndonCallCommand, AndonCallResponse>
{
    public async Task<AndonCallResponse> HandleAsync(AcknowledgeAndonCallCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var call = await andon.GetCallForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Andon call {command.Id} not found.");
        var envelope = events.Create(EventTypes.AndonCallAcknowledged, "andon_call", call.Id, new { Before = new { call.Status } });
        var after = call.Acknowledge(envelope.OccurredAt, envelope.ActorId);
        await andon.UpdateCallAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AndonCallResponse(after.Id, after.Status);
    }
}

internal sealed class ResolveAndonCallHandler(IUnitOfWork unitOfWork, IAndonRepository andon, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<ResolveAndonCallCommand, AndonCallResponse>
{
    public async Task<AndonCallResponse> HandleAsync(ResolveAndonCallCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var call = await andon.GetCallForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Andon call {command.Id} not found.");
        var envelope = events.Create(EventTypes.AndonCallResolved, "andon_call", call.Id, new { Before = new { call.Status }, command.Resolution });
        var after = call.Resolve(envelope.OccurredAt, envelope.ActorId, command.Resolution);
        await andon.UpdateCallAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AndonCallResponse(after.Id, after.Status);
    }
}

/// <summary>The site's call types, in their order.</summary>
public sealed record ListAndonTypesQuery(Guid SiteId) : IQuery<IReadOnlyList<AndonTypeDto>>;

/// <summary>
/// The site's andon calls (#125): those not resolved when <paramref name="OpenOnly"/>, else those raised in the period; on one line when
/// given. How long each waited and whether it is escalated are worked out now.
/// </summary>
public sealed record ListAndonCallsQuery(Guid SiteId, bool OpenOnly, DateTimeOffset? From = null, DateTimeOffset? To = null, Guid? LineId = null)
    : IQuery<IReadOnlyList<AndonCallDto>>;

internal sealed class ListAndonTypesHandler(IAndonReadStore store) : IQueryHandler<ListAndonTypesQuery, IReadOnlyList<AndonTypeDto>>
{
    public Task<IReadOnlyList<AndonTypeDto>> HandleAsync(ListAndonTypesQuery query, CancellationToken cancellationToken) =>
        store.ListTypesAsync(query.SiteId, cancellationToken);
}

internal sealed class ListAndonCallsHandler(IAndonReadStore store, TimeProvider time) : IQueryHandler<ListAndonCallsQuery, IReadOnlyList<AndonCallDto>>
{
    public async Task<IReadOnlyList<AndonCallDto>> HandleAsync(ListAndonCallsQuery query, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var calls = await store.ListCallsAsync(query.SiteId, query.OpenOnly, query.From, query.To, query.LineId, cancellationToken);
        return [.. calls.Select(c => AndonView.Figure(c, now))];
    }
}

/// <summary>A call's escalation and waiting time as of now (#125). Pure.</summary>
public static class AndonView
{
    public static AndonCallDto Figure(AndonCallDto call, DateTimeOffset now)
    {
        var domain = new AndonCall(call.Id, call.SiteId, call.LineId, call.TypeId, call.Status, call.Note, call.RaisedAt, call.RaisedBy, call.RaisedFrom,
            call.AcknowledgedAt, call.AcknowledgedBy, call.ResolvedAt, call.ResolvedBy, call.Resolution);
        var type = new AndonType(call.TypeId, call.SiteId, call.TypeCode, call.TypeName, call.EscalateAfterMinutes, 0, true);
        return call with
        {
            IsEscalated = AndonRules.IsEscalated(domain, type, now),
            WaitedSeconds = (int)Math.Max(0, AndonRules.Waited(domain, now).TotalSeconds),
        };
    }
}

/// <summary>How fast the site's calls for help were answered (#159), over calls raised in [From, To): at most 92 days.</summary>
public sealed record GetAndonResponseTimesQuery(Guid SiteId, DateTimeOffset From, DateTimeOffset To) : IQuery<AndonResponseTimesDto>;

internal sealed class GetAndonResponseTimesHandler(IAndonReadStore store, TimeProvider time) : IQueryHandler<GetAndonResponseTimesQuery, AndonResponseTimesDto>
{
    public async Task<AndonResponseTimesDto> HandleAsync(GetAndonResponseTimesQuery query, CancellationToken cancellationToken)
    {
        if (query.To <= query.From || query.To - query.From > AndonResponseTimes.LongestPeriod)
        {
            throw new DomainRuleException($"Choose a period of at most {AndonResponseTimes.LongestPeriod.TotalDays:0} days.");
        }

        var now = time.GetUtcNow();
        var calls = await store.ListCallsRaisedAsync(query.SiteId, query.From, query.To, cancellationToken);
        return AndonResponseTimes.Summarize([.. calls.Select(c => AndonView.Figure(c, now))], query.From, query.To);
    }
}

/// <summary>Response times from a period's calls (#159). Pure.</summary>
public static class AndonResponseTimes
{
    public static readonly TimeSpan LongestPeriod = TimeSpan.FromDays(92);

    public const int SlowestShown = 10;

    /// <summary>Rows by line and kind of help, the total and the slowest calls.</summary>
    /// <param name="calls">The period's calls, figured as of now (<see cref="AndonView.Figure"/>): their waiting time is to now while open.</param>
    /// <param name="from">The period's start, as asked.</param>
    /// <param name="to">The period's end, as asked.</param>
    public static AndonResponseTimesDto Summarize(IReadOnlyList<AndonCallDto> calls, DateTimeOffset from, DateTimeOffset to)
    {
        var rows = calls
            .GroupBy(c => (c.LineId, c.TypeId))
            .Select(g => Row([.. g]) with { LineId = g.Key.LineId, LineCode = g.First().LineCode, TypeId = g.Key.TypeId, TypeName = g.First().TypeName })
            .OrderBy(r => r.LineCode, StringComparer.Ordinal)
            .ThenBy(r => r.TypeName, StringComparer.Ordinal)
            .ToList();
        return new AndonResponseTimesDto
        {
            From = from,
            To = to,
            Rows = rows,
            Total = calls.Count == 0 ? null : Row(calls),
            Slowest = [.. calls.OrderByDescending(c => c.WaitedSeconds).ThenBy(c => c.RaisedAt).Take(SlowestShown)],
        };
    }

    private static AndonResponseRowDto Row(IReadOnlyList<AndonCallDto> calls)
    {
        var answered = calls.Where(c => c.AcknowledgedAt is not null).Select(c => (c.AcknowledgedAt!.Value - c.RaisedAt).TotalSeconds).ToList();
        var resolved = calls.Where(c => c.ResolvedAt is not null).Select(c => (c.ResolvedAt!.Value - c.RaisedAt).TotalSeconds).ToList();
        return new AndonResponseRowDto
        {
            Calls = calls.Count,
            Escalated = calls.Count(c => c.WaitedSeconds >= c.EscalateAfterMinutes * 60),
            StillOpen = calls.Count(c => c.ResolvedAt is null),
            AverageAnswerSeconds = answered.Count == 0 ? null : (int)Math.Round(answered.Average()),
            LongestAnswerSeconds = answered.Count == 0 ? null : (int)Math.Round(answered.Max()),
            AverageResolveSeconds = resolved.Count == 0 ? null : (int)Math.Round(resolved.Average()),
        };
    }
}
