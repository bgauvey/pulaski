using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>A stop as coding sees it (D-050).</summary>
public sealed record StopRecord(
    Guid Id, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, Guid EquipmentId, string EquipmentCode, Guid? MappedReasonCodeId, Guid? OperatorReasonCodeId,
    bool IsMicroStop, string? Comment, bool IsSuperseded = false);

/// <summary>A reason an operator may choose for a stop, with whether it needs a comment.</summary>
public sealed record CodingReason(Guid Id, bool RequiresComment);

/// <summary>Write side for coding stops (D-050).</summary>
public interface IStopRepository
{
    Task<StopRecord?> GetStopForUpdateAsync(Guid id, DateTimeOffset startedAt, CancellationToken cancellationToken);

    /// <summary>The utilization reasons that may be chosen for the equipment's stops (see <see cref="IStopReadStore.ListCodingReasonsAsync"/>).</summary>
    Task<IReadOnlyList<CodingReason>> ListCodingReasonsAsync(Guid equipmentId, CancellationToken cancellationToken);

    Task CodeStopAsync(Guid id, DateTimeOffset startedAt, Guid reasonCodeId, string? comment, string codedBy, Guid? codedByUserId, DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <summary>Read side for machine status and stops (D-050).</summary>
public interface IStopReadStore
{
    /// <summary>Every piece of equipment of the site that tracks state, in tree order, with what it last reported.</summary>
    Task<IReadOnlyList<MachineStatusRow>> ListMachineStatusAsync(Guid siteId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<DowntimeDto>> ListStopsAsync(StopFilter filter, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The reasons that may be chosen for the equipment's stops: with reasons ticked on its profile, those and the ones its codes and
    /// states map to; with none ticked, or no profile, every active utilization reason of its site.
    /// </summary>
    Task<IReadOnlyList<ReasonCodeDto>> ListCodingReasonsAsync(Guid equipmentId, CancellationToken cancellationToken);
}

/// <summary>A row of the machine status board before lines take their state from their machines.</summary>
public sealed record MachineStatusRow(MachineStatusDto Status, Guid? ConstraintEquipmentId);

/// <summary>Which stops to list; <c>IncludeSuperseded</c> adds those replaced by a split or merge (D-069), which are history and not counted.</summary>
public sealed record StopFilter(
    Guid SiteId, Guid? EquipmentId, DateTimeOffset From, DateTimeOffset To, bool UncodedOnly, bool IncludeMicroStops, bool IncludeSuperseded = false);

/// <summary>An operator codes a stop (D-050): confirms the reason its code maps to, or gives another. Both are kept (D-005).</summary>
public sealed record CodeDowntimeCommand(Guid Id, DateTimeOffset StartedAt, Guid ReasonCodeId, string? Comment) : ICommand<DowntimeCodedResponse>;

internal sealed class CodeDowntimeHandler(IUnitOfWork unitOfWork, IStopRepository stops, IEventLog eventLog, IEventFactory events, IRequestContext request, TimeProvider time)
    : ICommandHandler<CodeDowntimeCommand, DowntimeCodedResponse>
{
    public async Task<DowntimeCodedResponse> HandleAsync(CodeDowntimeCommand command, CancellationToken cancellationToken)
    {
        var comment = string.IsNullOrWhiteSpace(command.Comment) ? null : command.Comment.Trim();
        new Validator()
            .Require(command.ReasonCodeId != Guid.Empty, "ReasonCodeId", "Choose a reason.")
            .Require(comment is null || comment.Length <= 1000, "Comment", "At most 1000 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var stop = await stops.GetStopForUpdateAsync(command.Id, command.StartedAt, cancellationToken) ?? throw new NotFoundException($"Stop {command.Id} not found.");
        if (stop.IsSuperseded)
        {
            throw new DomainRuleException("This stop has been split or merged: code the stops that replaced it.");
        }

        if (stop.IsMicroStop)
        {
            throw new DomainRuleException("A micro-stop isn't coded: it is shorter than the machine's micro-stop threshold.");
        }

        var reason = (await stops.ListCodingReasonsAsync(stop.EquipmentId, cancellationToken)).FirstOrDefault(r => r.Id == command.ReasonCodeId)
            ?? throw new DomainRuleException($"Choose one of the reasons offered for {stop.EquipmentCode}.");
        if (reason.RequiresComment && comment is null)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Comment"] = ["This reason needs a comment."] });
        }

        if (stop.OperatorReasonCodeId == command.ReasonCodeId && stop.Comment == comment)
        {
            return new DowntimeCodedResponse(stop.Id);
        }

        var envelope = events.Create(EventTypes.DowntimeCoded, "equipment", stop.EquipmentId, new
        {
            StopId = stop.Id,
            stop.StartedAt,
            stop.MappedReasonCodeId,
            Before = new { ReasonCodeId = stop.OperatorReasonCodeId, stop.Comment },
            After = new { command.ReasonCodeId, Comment = comment },
            Confirmed = command.ReasonCodeId == stop.MappedReasonCodeId,
        });
        await stops.CodeStopAsync(stop.Id, stop.StartedAt, command.ReasonCodeId, comment, request.ActorId, request.UserId, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DowntimeCodedResponse(stop.Id);
    }
}

public sealed record GetMachineStatusQuery(Guid SiteId) : IQuery<IReadOnlyList<MachineStatusDto>>;

public sealed record ListStopsQuery(StopFilter Filter) : IQuery<IReadOnlyList<DowntimeDto>>;

public sealed record ListCodingReasonsQuery(Guid EquipmentId) : IQuery<IReadOnlyList<ReasonCodeDto>>;

/// <summary>The status board: each machine's state, and each line's from its machines as it is set to (D-050).</summary>
internal sealed class GetMachineStatusHandler(IStopReadStore store, TimeProvider time) : IQueryHandler<GetMachineStatusQuery, IReadOnlyList<MachineStatusDto>>
{
    public async Task<IReadOnlyList<MachineStatusDto>> HandleAsync(GetMachineStatusQuery query, CancellationToken cancellationToken)
    {
        var rows = await store.ListMachineStatusAsync(query.SiteId, time.GetUtcNow(), cancellationToken);
        var byId = rows.ToDictionary(r => r.Status.EquipmentId);
        var children = rows.Where(r => r.Status.ParentId is not null).ToLookup(r => r.Status.ParentId!.Value);
        var derived = new Dictionary<Guid, MachineStatusDto>();

        MachineStatusDto Resolve(Guid id, int depth)
        {
            if (derived.TryGetValue(id, out var done))
            {
                return done;
            }

            var row = byId[id];
            var status = row.Status;
            if (depth < 16)
            {
                switch (status.StateDerivation)
                {
                    case StateDerivations.Series or StateDerivations.Parallel:
                        var machines = Below(id).Select(m => Resolve(m, depth + 1)).Where(m => m.Category is not null).ToList();
                        status = status with
                        {
                            Category = LineStates.Derive(status.StateDerivation, [.. machines.Select(m => m.Category!)]),
                            IsDerived = true,
                        };
                        break;
                    case StateDerivations.ConstraintMachine when row.ConstraintEquipmentId is { } constraint && byId.ContainsKey(constraint):
                        var machine = Resolve(constraint, depth + 1);
                        status = status with
                        {
                            Category = machine.Category,
                            PackMlState = machine.PackMlState,
                            PackMlName = machine.PackMlName,
                            StateSince = machine.StateSince,
                            IsDerived = true,
                        };
                        break;
                }
            }

            derived[id] = status;
            return status;
        }

        // A line's machines: the equipment below it that tracks state, looking through equipment that doesn't.
        IEnumerable<Guid> Below(Guid id) => children[id].Select(c => c.Status.EquipmentId);

        return [.. rows.Select(r => Resolve(r.Status.EquipmentId, 0))];
    }
}

internal sealed class ListStopsHandler(IStopReadStore store, TimeProvider time) : IQueryHandler<ListStopsQuery, IReadOnlyList<DowntimeDto>>
{
    public Task<IReadOnlyList<DowntimeDto>> HandleAsync(ListStopsQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.Filter.To > query.Filter.From, "To", "After From.")
            .Require(query.Filter.To - query.Filter.From <= TimeSpan.FromDays(31), "To", "At most 31 days at a time.")
            .ThrowIfInvalid();
        return store.ListStopsAsync(query.Filter, time.GetUtcNow(), cancellationToken);
    }
}

internal sealed class ListCodingReasonsHandler(IStopReadStore store) : IQueryHandler<ListCodingReasonsQuery, IReadOnlyList<ReasonCodeDto>>
{
    public Task<IReadOnlyList<ReasonCodeDto>> HandleAsync(ListCodingReasonsQuery query, CancellationToken cancellationToken) =>
        store.ListCodingReasonsAsync(query.EquipmentId, cancellationToken);
}
