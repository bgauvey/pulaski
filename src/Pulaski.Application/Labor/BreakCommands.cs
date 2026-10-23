using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Labor;
using Pulaski.Domain;
using Pulaski.Domain.Labor;

namespace Pulaski.Application.Labor;

/// <summary>
/// Starts a break for a person (D-047), or records one already over when <paramref name="EndedAt"/> is given. Without
/// <paramref name="PersonId"/> it is the signed-in user's own. It must start while they are on a line.
/// </summary>
public sealed record StartBreakCommand(Guid? PersonId, bool IsPaid, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Note) : ICommand<LaborResponse>;

/// <summary>Ends a break; <paramref name="OwnOnly"/> when the signed-in user ends their own.</summary>
public sealed record EndBreakCommand(Guid BreakId, long ExpectedVersion, DateTimeOffset? EndedAt, bool OwnOnly) : ICommand<LaborResponse>;

/// <summary>Changes a break whose shift isn't approved yet.</summary>
public sealed record UpdateBreakCommand(Guid BreakId, long ExpectedVersion, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool IsPaid, string? Note)
    : ICommand<LaborResponse>;

/// <summary>Corrects a break of an approved shift: a new break replaces it, or with <paramref name="Void"/> removes it.</summary>
public sealed record CorrectBreakCommand(Guid BreakId, bool Void, DateTimeOffset StartedAt, DateTimeOffset EndedAt, bool IsPaid, string Note)
    : ICommand<LaborResponse>;

internal static class BreakWriter
{
    /// <summary>
    /// Checks a break's times for a person: within the rules, starting while they were on a line, and not overlapping
    /// another of their breaks. Returns the shift it belongs to: that of the session it starts in.
    /// </summary>
    public static async Task<ShiftKey> CheckAsync(
        ILaborRepository labor, PersonRecord person, DateTimeOffset start, DateTimeOffset? end, Guid? except, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (LaborRules.BreakProblem(start, end, now) is { } problem)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Break"] = [problem] });
        }

        var on = await labor.ListSessionsOfPersonAtAsync(person.Id, start, cancellationToken);
        if (on.Count == 0)
        {
            throw new DomainRuleException($"{person.DisplayName} isn't on a line at {start:yyyy-MM-dd HH:mm} UTC; a break is taken from time on a line.");
        }

        var others = (await labor.ListBreaksOfPersonAsync(person.Id, start, end ?? DateTimeOffset.MaxValue, cancellationToken)).Where(b => b.Id != except).ToList();
        if (others.Count > 0)
        {
            throw new DomainRuleException($"{person.DisplayName} is already on a break then ({others[0].StartedAt:HH:mm} UTC); end or change that one first.");
        }

        return on[0].Shift;
    }

    public static async Task EnsureOpenAsync(ILaborRepository labor, ShiftKey shift, CancellationToken cancellationToken)
    {
        if (await labor.IsApprovedAsync(shift, cancellationToken))
        {
            throw new DomainRuleException($"The labor of {LaborWriter.Describe(shift)} is approved already; a supervisor can correct it.");
        }
    }

    /// <summary>
    /// Ends the person's open break at <paramref name="end"/> when they are no longer on any line (D-047), so a break never
    /// outlasts their time. Run after their session ended.
    /// </summary>
    public static async Task EndIfOffAsync(
        ILaborRepository labor, IEventLog eventLog, IEventFactory events, Guid personId, DateTimeOffset end, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if ((await labor.ListOpenSessionsOfPersonForUpdateAsync(personId, cancellationToken)).Count > 0
            || await labor.GetOpenBreakForUpdateAsync(personId, cancellationToken) is not { } open)
        {
            return;
        }

        var ended = open.Value with { EndedAt = end > open.Value.StartedAt ? end : open.Value.StartedAt.AddSeconds(1) };
        await labor.UpdateBreakAsync(ended, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborBreakEnded, "labor_break", ended.Id, new { ended.EndedAt }), cancellationToken);
    }
}

internal sealed class StartBreakHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IRequestContext request, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<StartBreakCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(StartBreakCommand command, CancellationToken cancellationToken)
    {
        if (command.PersonId == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["PersonId"] = ["Choose who."] });
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // The person is locked, so two breaks for them can't start at once.
        var person = command.PersonId is { } personId
            ? (await labor.GetPersonForUpdateAsync(personId, cancellationToken))?.Value ?? throw new NotFoundException($"Person {personId} not found.")
            : (request.UserId is { } userId ? await labor.GetPersonOfUserForUpdateAsync(userId, cancellationToken) : null)?.Value
              ?? throw new DomainRuleException("You aren't set up as a person whose time is recorded; ask a supervisor.");

        var start = command.StartedAt ?? now;
        var shift = await BreakWriter.CheckAsync(labor, person, start, command.EndedAt, null, now, cancellationToken);
        await BreakWriter.EnsureOpenAsync(labor, shift, cancellationToken);

        var source = command.PersonId is null ? LaborSources.Self : LaborSources.Supervisor;
        var started = new BreakRecord(ids.NewId(), person.SiteId, person.Id, start, command.EndedAt, command.IsPaid, shift.ProductionDate, shift.ShiftScheduleId,
            shift.ShiftCode, source, null, false, LaborWriter.Clean(command.Note), request.ActorId);
        var version = await labor.InsertBreakAsync(started, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborBreakStarted, "labor_break", started.Id, new { After = started }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(started.Id, version, []);
    }
}

internal sealed class EndBreakHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IRequestContext request, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<EndBreakCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(EndBreakCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await labor.GetBreakForUpdateAsync(command.BreakId, cancellationToken) ?? throw new NotFoundException($"Break {command.BreakId} not found.");
        if (command.OwnOnly)
        {
            var me = request.UserId is { } userId ? (await labor.GetPersonOfUserForUpdateAsync(userId, cancellationToken))?.Value : null;
            if (me?.Id != current.Value.PersonId)
            {
                throw new ForbiddenException("You can only end your own break.");
            }
        }

        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("That break was changed by someone else. Reload and try again.");
        }

        if (current.Value.EndedAt is not null)
        {
            throw new DomainRuleException("That break has ended already.");
        }

        var end = command.EndedAt ?? now;
        if (LaborRules.BreakProblem(current.Value.StartedAt, end, now) is { } problem)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["EndedAt"] = [problem] });
        }

        var ended = current.Value with { EndedAt = end };
        var version = await labor.UpdateBreakAsync(ended, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborBreakEnded, "labor_break", ended.Id, new { ended.EndedAt }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(ended.Id, version, []);
    }
}

internal sealed class UpdateBreakHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<UpdateBreakCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(UpdateBreakCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await labor.GetBreakForUpdateAsync(command.BreakId, cancellationToken) ?? throw new NotFoundException($"Break {command.BreakId} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("That break was changed by someone else. Reload and try again.");
        }

        await BreakWriter.EnsureOpenAsync(labor, current.Value.Shift, cancellationToken);
        var person = (await labor.GetPersonForUpdateAsync(current.Value.PersonId, cancellationToken))!.Value;
        var shift = await BreakWriter.CheckAsync(labor, person, command.StartedAt, command.EndedAt, current.Value.Id, now, cancellationToken);
        if (shift != current.Value.Shift)
        {
            await BreakWriter.EnsureOpenAsync(labor, shift, cancellationToken);
        }

        var updated = current.Value with
        {
            StartedAt = command.StartedAt,
            EndedAt = command.EndedAt,
            IsPaid = command.IsPaid,
            ProductionDate = shift.ProductionDate,
            ShiftScheduleId = shift.ShiftScheduleId,
            ShiftCode = shift.ShiftCode,
            Note = LaborWriter.Clean(command.Note),
        };
        if (updated == current.Value)
        {
            return new LaborResponse(updated.Id, current.Version, []);
        }

        var version = await labor.UpdateBreakAsync(updated, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborBreakChanged, "labor_break", updated.Id, new { Before = current.Value, After = updated }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(updated.Id, version, []);
    }
}

internal sealed class CorrectBreakHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IRequestContext request, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<CorrectBreakCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(CorrectBreakCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var note = LaborWriter.Clean(command.Note) ?? throw new ValidationException(new Dictionary<string, string[]> { ["Note"] = ["Say why: the shift was approved already."] });
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var original = (await labor.GetBreakForUpdateAsync(command.BreakId, cancellationToken))?.Value ?? throw new NotFoundException($"Break {command.BreakId} not found.");
        if (!await labor.IsApprovedAsync(original.Shift, cancellationToken))
        {
            throw new DomainRuleException("Its shift isn't approved yet: change the break instead.");
        }

        if (await labor.IsBreakCorrectedAsync(original.Id, cancellationToken))
        {
            throw new DomainRuleException("That break was corrected already; correct the correction.");
        }

        if (!command.Void)
        {
            if (LaborRules.BreakProblem(command.StartedAt, command.EndedAt, now) is { } problem)
            {
                throw new ValidationException(new Dictionary<string, string[]> { ["Break"] = [problem] });
            }

            var person = (await labor.GetPersonForUpdateAsync(original.PersonId, cancellationToken))!.Value;
            if ((await labor.ListSessionsOfPersonAtAsync(person.Id, command.StartedAt, cancellationToken)).Count == 0)
            {
                throw new DomainRuleException($"{person.DisplayName} isn't on a line then; a break is taken from time on a line.");
            }

            if ((await labor.ListBreaksOfPersonAsync(person.Id, command.StartedAt, command.EndedAt, cancellationToken)).Any(b => b.Id != original.Id))
            {
                throw new DomainRuleException($"{person.DisplayName} is already on another break then.");
            }
        }

        // The correction stays in the original's shift, already approved: approving it is the supervisor's act.
        var correction = original with
        {
            Id = ids.NewId(),
            StartedAt = command.Void ? original.StartedAt : command.StartedAt,
            EndedAt = command.Void ? original.EndedAt ?? original.StartedAt.AddSeconds(1) : command.EndedAt,
            IsPaid = command.Void ? original.IsPaid : command.IsPaid,
            Source = LaborSources.Correction,
            CorrectsBreakId = original.Id,
            IsVoid = command.Void,
            Note = note,
            RecordedBy = request.ActorId,
        };
        var version = await labor.InsertBreakAsync(correction, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborBreakCorrected, "labor_break", original.Id, new { Before = original, After = correction }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(correction.Id, version, []);
    }
}
