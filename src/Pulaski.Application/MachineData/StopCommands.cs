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

    /// <summary>
    /// Up to <paramref name="take"/> stops, newest first: by start, a current stop before the ones it replaced, then by id. With
    /// <paramref name="after"/>, the ones that come after that stop in this order.
    /// </summary>
    Task<IReadOnlyList<DowntimeDto>> ListStopsAsync(StopFilter filter, StopCursor? after, int take, DateTimeOffset now, CancellationToken cancellationToken);

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

/// <summary>A page of stops (#10): up to <paramref name="Limit"/> (default 500, at most 1000), continuing from <paramref name="After"/>.</summary>
public sealed record ListStopsQuery(StopFilter Filter, string? After = null, int? Limit = null) : IQuery<StopPageDto>;

/// <summary>
/// Where a page of stops ended: the last stop's start, whether it is current (not replaced), and its id, which together
/// order every stop (#10). Passed to callers as an opaque string.
/// </summary>
public sealed record StopCursor(DateTimeOffset StartedAt, bool IsCurrent, Guid Id)
{
    public static StopCursor Of(DowntimeDto stop) => new(stop.StartedAt, stop.SupersededAt is null, stop.Id);

    public string Encode() =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{StartedAt.UtcTicks}.{(IsCurrent ? 1 : 0)}.{Id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static StopCursor? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('.');
            if (parts.Length == 3 && long.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                && parts[1] is "0" or "1" && Guid.TryParseExact(parts[2], "N", out var id))
            {
                return new StopCursor(new DateTimeOffset(ticks, TimeSpan.Zero), parts[1] == "1", id);
            }
        }
        catch (FormatException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        throw new ValidationException(new Dictionary<string, string[]> { ["After"] = ["Not a page of this list: start again from the first page."] });
    }
}

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

internal sealed class ListStopsHandler(IStopReadStore store, TimeProvider time) : IQueryHandler<ListStopsQuery, StopPageDto>
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 1000;

    public async Task<StopPageDto> HandleAsync(ListStopsQuery query, CancellationToken cancellationToken)
    {
        var limit = query.Limit ?? DefaultLimit;
        new Validator()
            .Require(query.Filter.To > query.Filter.From, "To", "After From.")
            .Require(query.Filter.To - query.Filter.From <= TimeSpan.FromDays(31), "To", "At most 31 days at a time.")
            .Require(limit is >= 1 and <= MaxLimit, "Limit", $"Between 1 and {MaxLimit}.")
            .ThrowIfInvalid();

        // One more than the page tells whether another page follows, without counting.
        var rows = await store.ListStopsAsync(query.Filter, StopCursor.Decode(query.After), limit + 1, time.GetUtcNow(), cancellationToken);
        var page = rows.Count > limit ? rows.Take(limit).ToList() : rows;
        return new StopPageDto { Stops = page, Next = rows.Count > limit ? StopCursor.Of(page[^1]).Encode() : null };
    }
}

internal sealed class ListCodingReasonsHandler(IStopReadStore store) : IQueryHandler<ListCodingReasonsQuery, IReadOnlyList<ReasonCodeDto>>
{
    public Task<IReadOnlyList<ReasonCodeDto>> HandleAsync(ListCodingReasonsQuery query, CancellationToken cancellationToken) =>
        store.ListCodingReasonsAsync(query.EquipmentId, cancellationToken);
}
