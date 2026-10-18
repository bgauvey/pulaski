using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Labor;
using Pulaski.Domain;
using Pulaski.Domain.Labor;

namespace Pulaski.Application.Labor;

/// <summary>What a new session is for (D-040). Without a start time it starts now.</summary>
public sealed record LaborInput(Guid EquipmentId, Guid? JobId, string LaborType, Guid? ReasonCodeId, decimal AllocationPct, DateTimeOffset? StartedAt, string? Note);

/// <summary>
/// Puts a person on a line (D-040). With <paramref name="PersonId"/> a supervisor records it for them; without, the signed-in
/// user clocks themselves on.
/// </summary>
public sealed record ClockOnCommand(Guid? PersonId, LaborInput Input) : ICommand<LaborResponse>;

/// <summary>Ends a session; <paramref name="OwnOnly"/> when the signed-in user ends their own.</summary>
public sealed record ClockOffCommand(Guid SessionId, long ExpectedVersion, DateTimeOffset? EndedAt, bool OwnOnly) : ICommand<LaborResponse>;

/// <summary>Checks people onto a line at once, usually the crew on shift: direct time, from now or the given time.</summary>
public sealed record CheckInCommand(Guid EquipmentId, IReadOnlyList<Guid> PersonIds, DateTimeOffset? StartedAt) : ICommand<LaborResponse>;

/// <summary>Ends every open session on the equipment.</summary>
public sealed record CheckOutCommand(Guid EquipmentId, DateTimeOffset? EndedAt) : ICommand<LaborResponse>;

/// <summary>Changes a session whose shift isn't approved yet.</summary>
public sealed record UpdateLaborSessionCommand(Guid SessionId, long ExpectedVersion, Guid EquipmentId, Guid? JobId, string LaborType, Guid? ReasonCodeId,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, decimal AllocationPct, string? Note) : ICommand<LaborResponse>;

/// <summary>Corrects a session of an approved shift: a new session replaces it, or with <paramref name="Void"/> removes it.</summary>
public sealed record CorrectLaborSessionCommand(Guid SessionId, bool Void, Guid EquipmentId, Guid? JobId, string LaborType, Guid? ReasonCodeId,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, decimal AllocationPct, string Note) : ICommand<LaborResponse>;

/// <summary>Approves a shift's labor (D-040). Open sessions in it end when it ended; from then on its sessions are locked.</summary>
public sealed record ApproveShiftCommand(ShiftKey Shift, string? Note) : ICommand<LaborResponse>;

internal static class LaborWriter
{
    /// <summary>Checks a session's fields and that its line, job and reason belong to the site.</summary>
    public static async Task<EquipmentShifts> CheckAsync(
        ILaborRepository labor, Guid siteId, Guid equipmentId, Guid? jobId, string laborType, Guid? reasonCodeId,
        DateTimeOffset start, DateTimeOffset? end, decimal allocationPct, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (LaborRules.Problem(laborType, reasonCodeId, jobId, start, end, allocationPct, now) is { } problem)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Session"] = [problem] });
        }

        var equipment = await labor.GetEquipmentShiftsAsync(equipmentId, cancellationToken) ?? throw new DomainRuleException("That equipment doesn't exist.");
        if (equipment.SiteId != siteId)
        {
            throw new DomainRuleException($"{equipment.Code} is at another site than the person.");
        }

        if (jobId is { } id && (await labor.GetJobAsync(id, cancellationToken))?.SiteId != siteId)
        {
            throw new DomainRuleException("Choose a job of the person's site.");
        }

        if (reasonCodeId is { } reason && !await labor.IsIndirectReasonAsync(reason, siteId, cancellationToken))
        {
            throw new DomainRuleException("Choose an active indirect labor reason.");
        }

        return equipment;
    }

    /// <summary>The shift a session starting then belongs to, which must not be approved yet.</summary>
    public static async Task<ShiftKey> OpenShiftAsync(ILaborRepository labor, EquipmentShifts equipment, Guid siteId, DateTimeOffset start, CancellationToken cancellationToken)
    {
        var (date, shift) = equipment.ShiftAt(start);
        var key = new ShiftKey(siteId, date, shift is null ? null : equipment.ScheduleId, shift?.Code);
        if (await labor.IsApprovedAsync(key, cancellationToken))
        {
            throw new DomainRuleException($"The labor of {Describe(key)} is approved already; a supervisor can correct it.");
        }

        return key;
    }

    /// <summary>Starts a session for a person, after the share-of-time and one-per-line checks (the person is locked).</summary>
    public static async Task<(SessionRecord Session, long Version)> StartAsync(
        ILaborRepository labor, PersonRecord person, LaborInput input, string source, string actor, DateTimeOffset now, Guid id, CancellationToken cancellationToken)
    {
        if (!person.IsActive)
        {
            throw new DomainRuleException($"{person.DisplayName} is switched off.");
        }

        var start = input.StartedAt ?? now;
        var equipment = await CheckAsync(labor, person.SiteId, input.EquipmentId, input.JobId, input.LaborType, input.ReasonCodeId, start, null, input.AllocationPct, now, cancellationToken);
        var open = await labor.ListOpenSessionsOfPersonForUpdateAsync(person.Id, cancellationToken);
        if (open.Any(s => s.EquipmentId == input.EquipmentId))
        {
            throw new DomainRuleException($"{person.DisplayName} is already on {equipment.Code}.");
        }

        if (LaborRules.AllocationProblem(open.Select(s => s.AllocationPct), input.AllocationPct) is { } problem)
        {
            throw new DomainRuleException($"{person.DisplayName}: {problem}");
        }

        var key = await OpenShiftAsync(labor, equipment, person.SiteId, start, cancellationToken);
        var session = new SessionRecord(id, person.SiteId, person.Id, input.EquipmentId, input.JobId, input.LaborType,
            input.LaborType == LaborTypes.Indirect ? input.ReasonCodeId : null, start, null, input.AllocationPct,
            key.ProductionDate, key.ShiftScheduleId, key.ShiftCode, source, null, false, Clean(input.Note), actor);
        return (session, await labor.InsertSessionAsync(session, now, cancellationToken));
    }

    public static string? Clean(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)];

    public static string Describe(ShiftKey key) => key.ShiftCode is null ? $"{key.ProductionDate:yyyy-MM-dd}" : $"shift {key.ShiftCode} on {key.ProductionDate:yyyy-MM-dd}";
}

internal sealed class ClockOnHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IRequestContext request, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<ClockOnCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(ClockOnCommand command, CancellationToken cancellationToken)
    {
        if (command.PersonId == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["PersonId"] = ["Choose who."] });
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var person = command.PersonId is { } personId
            ? (await labor.GetPersonForUpdateAsync(personId, cancellationToken))?.Value ?? throw new NotFoundException($"Person {personId} not found.")
            : (request.UserId is { } userId ? await labor.GetPersonOfUserForUpdateAsync(userId, cancellationToken) : null)?.Value
              ?? throw new DomainRuleException("You aren't set up as a person whose time is recorded; ask a supervisor.");

        var source = command.PersonId is null ? LaborSources.Self : LaborSources.Supervisor;
        var (session, version) = await LaborWriter.StartAsync(labor, person, command.Input, source, request.ActorId, timeProvider.GetUtcNow(), ids.NewId(), cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborStarted, "labor_session", session.Id, new { After = session }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(session.Id, version, []);
    }
}

internal sealed class CheckInHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IRequestContext request, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<CheckInCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(CheckInCommand command, CancellationToken cancellationToken)
    {
        if (command.PersonIds.Count == 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["PersonIds"] = ["Choose who to check in."] });
        }

        var now = timeProvider.GetUtcNow();
        var notes = new List<string>();
        var started = 0;
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // Everyone is locked at once, in the order SQL gives, so two supervisors checking overlapping people in can't deadlock.
        var people = await labor.GetPeopleForUpdateAsync([.. command.PersonIds.Distinct()], cancellationToken);
        if (people.Count != command.PersonIds.Distinct().Count())
        {
            throw new NotFoundException("Some of those people don't exist.");
        }

        foreach (var person in people)
        {
            if ((await labor.ListOpenSessionsOfPersonForUpdateAsync(person.Id, cancellationToken)).Any(s => s.EquipmentId == command.EquipmentId))
            {
                notes.Add($"{person.DisplayName} was already on.");
                continue;
            }

            var (session, _) = await LaborWriter.StartAsync(
                labor, person, new LaborInput(command.EquipmentId, null, LaborTypes.Direct, null, 100, command.StartedAt, null),
                LaborSources.Crew, request.ActorId, now, ids.NewId(), cancellationToken);
            await eventLog.AppendAsync(events.Create(EventTypes.LaborStarted, "labor_session", session.Id, new { After = session }), cancellationToken);
            started++;
        }

        await transaction.CommitAsync(cancellationToken);
        notes.Insert(0, $"Checked {started} {(started == 1 ? "person" : "people")} in.");
        return new LaborResponse(command.EquipmentId, 0, notes);
    }
}

internal sealed class ClockOffHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IRequestContext request, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<ClockOffCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(ClockOffCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await labor.GetSessionForUpdateAsync(command.SessionId, cancellationToken) ?? throw new NotFoundException($"Session {command.SessionId} not found.");
        if (command.OwnOnly)
        {
            var me = request.UserId is { } userId ? (await labor.GetPersonOfUserForUpdateAsync(userId, cancellationToken))?.Value : null;
            if (me?.Id != current.Value.PersonId)
            {
                throw new ForbiddenException("You can only clock yourself off.");
            }
        }

        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("That session was changed by someone else. Reload and try again.");
        }

        if (current.Value.EndedAt is not null)
        {
            throw new DomainRuleException("That session has ended already.");
        }

        var end = command.EndedAt ?? now;
        if (end < current.Value.StartedAt || end > now + LaborRules.ClockSkew)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["EndedAt"] = ["Between when it started and now."] });
        }

        var ended = current.Value with { EndedAt = end };
        var version = await labor.UpdateSessionAsync(ended, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborEnded, "labor_session", ended.Id, new { ended.EndedAt }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(ended.Id, version, []);
    }
}

internal sealed class CheckOutHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<CheckOutCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(CheckOutCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var end = command.EndedAt ?? now;
        if (end > now + LaborRules.ClockSkew)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["EndedAt"] = ["Not ahead of now."] });
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var open = await labor.ListOpenSessionsOnEquipmentForUpdateAsync(command.EquipmentId, cancellationToken);
        foreach (var session in open)
        {
            var ended = session.Value with { EndedAt = end < session.Value.StartedAt ? session.Value.StartedAt : end };
            await labor.UpdateSessionAsync(ended, now, cancellationToken);
            await eventLog.AppendAsync(events.Create(EventTypes.LaborEnded, "labor_session", ended.Id, new { ended.EndedAt }), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(command.EquipmentId, 0, [$"Checked {open.Count} {(open.Count == 1 ? "person" : "people")} out."]);
    }
}

internal sealed class UpdateLaborSessionHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<UpdateLaborSessionCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(UpdateLaborSessionCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await labor.GetSessionForUpdateAsync(command.SessionId, cancellationToken) ?? throw new NotFoundException($"Session {command.SessionId} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("That session was changed by someone else. Reload and try again.");
        }

        if (await labor.IsApprovedAsync(current.Value.Shift, cancellationToken))
        {
            throw new DomainRuleException($"The labor of {LaborWriter.Describe(current.Value.Shift)} is approved; correct it instead.");
        }

        var equipment = await LaborWriter.CheckAsync(labor, current.Value.SiteId, command.EquipmentId, command.JobId, command.LaborType, command.ReasonCodeId,
            command.StartedAt, command.EndedAt, command.AllocationPct, now, cancellationToken);

        // A new start can move it into another shift; that one must be open too.
        var key = command.StartedAt == current.Value.StartedAt && command.EquipmentId == current.Value.EquipmentId
            ? current.Value.Shift
            : await LaborWriter.OpenShiftAsync(labor, equipment, current.Value.SiteId, command.StartedAt, cancellationToken);

        if (command.EndedAt is null && current.Value.EndedAt is null && command.AllocationPct != current.Value.AllocationPct)
        {
            var others = (await labor.ListOpenSessionsOfPersonForUpdateAsync(current.Value.PersonId, cancellationToken)).Where(s => s.Id != current.Value.Id);
            if (LaborRules.AllocationProblem(others.Select(s => s.AllocationPct), command.AllocationPct) is { } problem)
            {
                throw new DomainRuleException(problem);
            }
        }

        var updated = current.Value with
        {
            EquipmentId = command.EquipmentId,
            JobId = command.JobId,
            LaborType = command.LaborType,
            ReasonCodeId = command.LaborType == LaborTypes.Indirect ? command.ReasonCodeId : null,
            StartedAt = command.StartedAt,
            EndedAt = command.EndedAt,
            AllocationPct = command.AllocationPct,
            ProductionDate = key.ProductionDate,
            ShiftScheduleId = key.ShiftScheduleId,
            ShiftCode = key.ShiftCode,
            Note = LaborWriter.Clean(command.Note),
        };
        if (updated == current.Value)
        {
            return new LaborResponse(updated.Id, current.Version, []);
        }

        var version = await labor.UpdateSessionAsync(updated, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborChanged, "labor_session", updated.Id, new { Before = current.Value, After = updated }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(updated.Id, version, []);
    }
}

internal sealed class CorrectLaborSessionHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IRequestContext request, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<CorrectLaborSessionCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(CorrectLaborSessionCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var note = LaborWriter.Clean(command.Note) ?? throw new ValidationException(new Dictionary<string, string[]> { ["Note"] = ["Say why: the shift was approved already."] });
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var original = (await labor.GetSessionForUpdateAsync(command.SessionId, cancellationToken))?.Value ?? throw new NotFoundException($"Session {command.SessionId} not found.");
        if (!await labor.IsApprovedAsync(original.Shift, cancellationToken))
        {
            throw new DomainRuleException("Its shift isn't approved yet: change the session instead.");
        }

        if (await labor.IsCorrectedAsync(original.Id, cancellationToken))
        {
            throw new DomainRuleException("That session was corrected already; correct the correction.");
        }

        if (!command.Void)
        {
            await LaborWriter.CheckAsync(labor, original.SiteId, command.EquipmentId, command.JobId, command.LaborType, command.ReasonCodeId,
                command.StartedAt, command.EndedAt, command.AllocationPct, now, cancellationToken);
        }

        // The correction stays in the original's shift, which is already approved: approving it is the supervisor's act.
        var correction = command.Void
            ? original with { Id = ids.NewId(), Source = LaborSources.Correction, CorrectsSessionId = original.Id, IsVoid = true, Note = note, RecordedBy = request.ActorId }
            : original with
            {
                Id = ids.NewId(),
                EquipmentId = command.EquipmentId,
                JobId = command.JobId,
                LaborType = command.LaborType,
                ReasonCodeId = command.LaborType == LaborTypes.Indirect ? command.ReasonCodeId : null,
                StartedAt = command.StartedAt,
                EndedAt = command.EndedAt,
                AllocationPct = command.AllocationPct,
                Source = LaborSources.Correction,
                CorrectsSessionId = original.Id,
                IsVoid = false,
                Note = note,
                RecordedBy = request.ActorId,
            };
        var version = await labor.InsertSessionAsync(correction, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborCorrected, "labor_session", original.Id, new { Before = original, After = correction }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(correction.Id, version, []);
    }
}

internal sealed class ApproveShiftHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IRequestContext request, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<ApproveShiftCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(ApproveShiftCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var key = command.Shift;
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // When the shift ended: from its schedule, or the end of the site-local day.
        DateTimeOffset end;
        if (key.ShiftScheduleId is { } scheduleId)
        {
            var schedule = await labor.GetScheduleAsync(scheduleId, cancellationToken) ?? throw new NotFoundException("That shift schedule doesn't exist.");
            var zone = TimeZoneInfo.FindSystemTimeZoneById(await labor.GetSiteTimeZoneAsync(key.SiteId, cancellationToken) ?? "UTC");
            var dayStart = new DateTimeOffset(key.ProductionDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(key.ProductionDate.ToDateTime(TimeOnly.MinValue)));
            var shift = schedule.Expand(zone, dayStart, dayStart.AddDays(1)).FirstOrDefault(o => o.Code == key.ShiftCode && o.ProductionDate == key.ProductionDate)
                ?? throw new DomainRuleException($"{schedule.Code} has no shift {key.ShiftCode} on {key.ProductionDate:yyyy-MM-dd}.");
            end = shift.End;
        }
        else
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(await labor.GetSiteTimeZoneAsync(key.SiteId, cancellationToken) ?? "UTC");
            var next = key.ProductionDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
            end = new DateTimeOffset(next, zone.GetUtcOffset(next));
        }

        if (end > now)
        {
            throw new DomainRuleException($"{char.ToUpperInvariant(LaborWriter.Describe(key)[0])}{LaborWriter.Describe(key)[1..]} hasn't ended yet; approve it once it has.");
        }

        if (await labor.IsApprovedAsync(key, cancellationToken))
        {
            throw new ConflictException($"The labor of {LaborWriter.Describe(key)} is approved already.");
        }

        var sessions = await labor.ListShiftSessionsForUpdateAsync(key, cancellationToken);
        var closed = 0;
        foreach (var open in sessions.Where(s => s.EndedAt is null))
        {
            var ended = open with { EndedAt = end < open.StartedAt ? open.StartedAt : end };
            await labor.UpdateSessionAsync(ended, now, cancellationToken);
            await eventLog.AppendAsync(events.Create(EventTypes.LaborEnded, "labor_session", ended.Id, new { ended.EndedAt }), cancellationToken);
            closed++;
        }

        var approvalId = ids.NewId();
        await labor.InsertApprovalAsync(approvalId, key, request.ActorId, request.UserId, now, LaborWriter.Clean(command.Note), cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborShiftApproved, "labor_approval", approvalId, new { Shift = key, Sessions = sessions.Count, Ended = closed }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(approvalId, 0, closed > 0 ? [$"{closed} open {(closed == 1 ? "session was" : "sessions were")} ended at the end of the shift."] : []);
    }
}
