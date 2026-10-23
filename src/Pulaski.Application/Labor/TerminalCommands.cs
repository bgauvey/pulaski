using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Labor;
using Pulaski.Domain;
using Pulaski.Domain.Labor;

namespace Pulaski.Application.Labor;

/// <summary>A shop-floor terminal as the commands see it (D-049).</summary>
public sealed record TerminalRecord(
    Guid Id, Guid SiteId, string Code, string Name, Guid ScopeEquipmentId, Guid? DefaultEquipmentId, string AuthMode, bool IsActive, DateTimeOffset? EnrolledAt)
{
    /// <summary>The actor its labor is recorded by, as on events and <c>recorded_by</c>.</summary>
    public string ActorId => $"terminal:{Code}";
}

/// <summary>A badge as the commands see it (D-049): only hashes, never the value or the PIN.</summary>
public sealed record BadgeRecord(
    Guid Id, Guid PersonId, byte[] BadgeHash, string? PinHash, DateTimeOffset IssuedAt, string IssuedBy, DateTimeOffset? RevokedAt, string? RevokedBy,
    int FailedPins, DateTimeOffset? LockedUntil, DateTimeOffset? LastUsedAt);

/// <summary>Hashes a badge value with a server key, so a stolen database doesn't give the (short, guessable) values away.</summary>
public interface IBadgeHasher
{
    byte[] Hash(string normalizedBadgeValue);
}

/// <summary>Write side for terminals and badges (D-049): runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ITerminalRepository
{
    Task<Versioned<TerminalRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task<long> InsertAsync(TerminalRecord terminal, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(TerminalRecord terminal, DateTimeOffset at, CancellationToken cancellationToken);

    Task SetEnrolmentCodeAsync(Guid id, byte[] codeHash, DateTimeOffset expiresAt, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Enrols the active terminal waiting with this code, if it hasn't expired: it takes the new secret and the code is used
    /// up. Null when no terminal is waiting with it.
    /// </summary>
    Task<TerminalRecord?> EnrolAsync(byte[] codeHash, byte[] secretHash, DateTimeOffset now, CancellationToken cancellationToken);

    Task TouchAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken);

    Task<BadgeRecord?> GetBadgeForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<BadgeRecord?> GetActiveBadgeOfPersonForUpdateAsync(Guid personId, CancellationToken cancellationToken);

    Task<BadgeRecord?> FindActiveBadgeForUpdateAsync(byte[] badgeHash, CancellationToken cancellationToken);

    Task InsertBadgeAsync(BadgeRecord badge, CancellationToken cancellationToken);

    Task UpdateBadgeAsync(BadgeRecord badge, CancellationToken cancellationToken);
}

/// <summary>Read side for terminals and badges (D-049).</summary>
public interface ITerminalReadStore
{
    Task<IReadOnlyList<TerminalDto>> ListAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The active, enrolled terminal holding this secret; null when there is none.</summary>
    Task<TerminalRecord?> FindBySecretAsync(byte[] secretHash, CancellationToken cancellationToken);

    /// <summary>The active lines that run jobs within the terminal's scope, with what runs on each.</summary>
    Task<IReadOnlyList<TerminalLineDto>> ListLinesAsync(Guid terminalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BadgeDto>> ListBadgesAsync(Guid siteId, CancellationToken cancellationToken);
}

/// <summary>Registers a terminal for a site, area or line (D-049); it works once enrolled.</summary>
public sealed record CreateTerminalCommand(string Code, string Name, Guid ScopeEquipmentId, Guid? DefaultEquipmentId, string AuthMode) : ICommand<CreatedResponse>;

/// <summary>Changes a terminal; switched off, its secret stops working at once.</summary>
public sealed record UpdateTerminalCommand(Guid Id, long ExpectedVersion, string Name, Guid ScopeEquipmentId, Guid? DefaultEquipmentId, string AuthMode, bool IsActive)
    : ICommand<VersionResponse>;

/// <summary>Issues a one-time enrolment code for a terminal, replacing any earlier one; enrolling with it replaces its secret.</summary>
public sealed record IssueEnrolmentCodeCommand(Guid Id) : ICommand<EnrolmentCodeResponse>;

/// <summary>Enrols a terminal with its one-time code; answers with the terminal's secret, given only this once.</summary>
public sealed record EnrolTerminalCommand(string Code) : ICommand<TerminalEnrolledResponse>;

/// <summary>Issues a person a badge, and a PIN for terminals that ask for one; their earlier badge is revoked.</summary>
public sealed record IssueBadgeCommand(Guid PersonId, string BadgeValue, string? Pin) : ICommand<CreatedResponse>;

public sealed record RevokeBadgeCommand(Guid BadgeId, string? Reason) : ICommand<VersionResponse>;

/// <summary>
/// Who a badge scanned at a terminal belongs to (D-049). Without a PIN at a terminal that asks for one, it only says so;
/// wrong PINs count against the badge and lock it for a while.
/// </summary>
public sealed record IdentifyBadgeCommand(TerminalRecord Terminal, string BadgeValue, string? Pin) : ICommand<BadgeIdentity>;

/// <summary>The person a badge identified, or that a PIN is needed first.</summary>
public sealed record BadgeIdentity(bool NeedsPin, Guid? PersonId);

/// <summary>Clocks the identified person on to a line the terminal offers, as direct time from now (D-049).</summary>
public sealed record TerminalClockOnCommand(TerminalRecord Terminal, Guid PersonId, Guid EquipmentId) : ICommand<LaborResponse>;

/// <summary>Ends one of the identified person's open sessions now.</summary>
public sealed record TerminalClockOffCommand(TerminalRecord Terminal, Guid PersonId, Guid SessionId) : ICommand<LaborResponse>;

/// <summary>Starts a break for the identified person now.</summary>
public sealed record TerminalStartBreakCommand(TerminalRecord Terminal, Guid PersonId, bool IsPaid) : ICommand<LaborResponse>;

/// <summary>Ends the identified person's open break now.</summary>
public sealed record TerminalEndBreakCommand(TerminalRecord Terminal, Guid PersonId) : ICommand<LaborResponse>;

/// <summary>What a terminal shows: its lines and, for an identified person, their open time and break.</summary>
public sealed record GetTerminalStatusQuery(TerminalRecord Terminal, Guid? PersonId) : IQuery<TerminalStatusDto>;

public sealed record ListTerminalsQuery(Guid SiteId) : IQuery<IReadOnlyList<TerminalDto>>;

public sealed record ListBadgesQuery(Guid SiteId) : IQuery<IReadOnlyList<BadgeDto>>;

internal static class TerminalWriter
{
    /// <summary>The scope and default line: equipment of one site (not the enterprise); the default line runs jobs inside the scope.</summary>
    public static async Task<Guid> CheckPlacementAsync(
        IEquipmentRepository equipment, Guid scopeId, Guid? defaultId, CancellationToken cancellationToken)
    {
        var scope = (await equipment.GetAsync(scopeId, RowLock.Share, cancellationToken))?.Value ?? throw new DomainRuleException("That equipment doesn't exist.");
        if (scope.SiteId is not { } siteId || !scope.IsActive)
        {
            throw new DomainRuleException($"{scope.Code} isn't active equipment in a site; a terminal belongs to a site, an area or a line.");
        }

        if (defaultId is { } id)
        {
            var line = (await equipment.GetAsync(id, RowLock.Share, cancellationToken))?.Value ?? throw new DomainRuleException("That line doesn't exist.");
            if (!line.CanRunJob || !line.IsActive || !await equipment.IsInSubtreeAsync(scopeId, id, cancellationToken))
            {
                throw new DomainRuleException($"{line.Code} isn't an active line within {scope.Code}; choose one of the lines the terminal offers.");
            }
        }

        return siteId;
    }

    public static void ValidateMode(string authMode) =>
        new Validator().Require(TerminalAuthModes.All.Contains(authMode), "AuthMode", "Badge, or badge and PIN.").ThrowIfInvalid();

    /// <summary>The identified person, locked, who must be active and of the terminal's site.</summary>
    public static async Task<PersonRecord> PersonAsync(ILaborRepository labor, TerminalRecord terminal, Guid personId, CancellationToken cancellationToken)
    {
        var person = (await labor.GetPersonForUpdateAsync(personId, cancellationToken))?.Value ?? throw new NotFoundException("That person doesn't exist.");
        if (person.SiteId != terminal.SiteId || !person.IsActive)
        {
            throw new ForbiddenException($"{person.DisplayName} can't record time at {terminal.Code}.");
        }

        return person;
    }
}

internal sealed class CreateTerminalHandler(
    IUnitOfWork unitOfWork, ITerminalRepository terminals, IEquipmentRepository equipment, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateTerminalCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateTerminalCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        new Validator().RequireText(code, "Code", 30).RequireText(name, "Name", 100).ThrowIfInvalid();
        TerminalWriter.ValidateMode(command.AuthMode);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var siteId = await TerminalWriter.CheckPlacementAsync(equipment, command.ScopeEquipmentId, command.DefaultEquipmentId, cancellationToken);
        if (await terminals.CodeExistsAsync(siteId, code, cancellationToken))
        {
            throw new ConflictException($"The site already has a terminal {code}.");
        }

        var terminal = new TerminalRecord(ids.NewId(), siteId, code, name, command.ScopeEquipmentId, command.DefaultEquipmentId, command.AuthMode, IsActive: true, null);
        var envelope = events.Create(EventTypes.TerminalCreated, "terminal", terminal.Id, new { After = terminal });
        var version = await terminals.InsertAsync(terminal, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(terminal.Id, version);
    }
}

internal sealed class UpdateTerminalHandler(IUnitOfWork unitOfWork, ITerminalRepository terminals, IEquipmentRepository equipment, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateTerminalCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateTerminalCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        new Validator().RequireText(name, "Name", 100).ThrowIfInvalid();
        TerminalWriter.ValidateMode(command.AuthMode);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await terminals.GetForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Terminal {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var siteId = await TerminalWriter.CheckPlacementAsync(equipment, command.ScopeEquipmentId, command.DefaultEquipmentId, cancellationToken);
        if (siteId != current.Value.SiteId)
        {
            throw new DomainRuleException($"{current.Value.Code} stays at its site; register another terminal for another site.");
        }

        var updated = current.Value with
        {
            Name = name,
            ScopeEquipmentId = command.ScopeEquipmentId,
            DefaultEquipmentId = command.DefaultEquipmentId,
            AuthMode = command.AuthMode,
            IsActive = command.IsActive,
        };
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.TerminalUpdated, "terminal", updated.Id, new { Before = current.Value, After = updated });
        var version = await terminals.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class IssueEnrolmentCodeHandler(IUnitOfWork unitOfWork, ITerminalRepository terminals, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<IssueEnrolmentCodeCommand, EnrolmentCodeResponse>
{
    public async Task<EnrolmentCodeResponse> HandleAsync(IssueEnrolmentCodeCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var terminal = (await terminals.GetForUpdateAsync(command.Id, cancellationToken))?.Value ?? throw new NotFoundException($"Terminal {command.Id} not found.");
        if (!terminal.IsActive)
        {
            throw new DomainRuleException($"{terminal.Code} is switched off; switch it on first.");
        }

        var code = TerminalRules.NewEnrolmentCode();
        var now = timeProvider.GetUtcNow();
        var expires = now + TerminalRules.EnrolmentCodeLifetime;
        await terminals.SetEnrolmentCodeAsync(terminal.Id, TerminalRules.Sha256(TerminalRules.NormalizeCode(code)), expires, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.TerminalEnrolmentCodeIssued, "terminal", terminal.Id, new { ExpiresAt = expires }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EnrolmentCodeResponse(code, expires);
    }
}

internal sealed class EnrolTerminalHandler(IUnitOfWork unitOfWork, ITerminalRepository terminals, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<EnrolTerminalCommand, TerminalEnrolledResponse>
{
    public async Task<TerminalEnrolledResponse> HandleAsync(EnrolTerminalCommand command, CancellationToken cancellationToken)
    {
        var code = TerminalRules.NormalizeCode(command.Code);
        if (code.Length != 10)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Code"] = ["Type the ten-character code, such as K7QM2-XW9PD."] });
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var secret = TerminalRules.NewSecret();
        var terminal = await terminals.EnrolAsync(TerminalRules.Sha256(code), TerminalRules.Sha256(secret), timeProvider.GetUtcNow(), cancellationToken)
            ?? throw new DomainRuleException("That code isn't valid: it is wrong, used already or more than 15 minutes old. Ask for a new one.");
        await eventLog.AppendAsync(events.Create(EventTypes.TerminalEnrolled, "terminal", terminal.Id, new { terminal.Code }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TerminalEnrolledResponse(terminal.Id, terminal.Code, terminal.Name, secret);
    }
}

internal sealed class IssueBadgeHandler(
    IUnitOfWork unitOfWork, ITerminalRepository terminals, ILaborRepository labor, IBadgeHasher badgeHasher, IPasswordHasher pinHasher, IRequestContext request,
    IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider) : ICommandHandler<IssueBadgeCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(IssueBadgeCommand command, CancellationToken cancellationToken)
    {
        var value = TerminalRules.NormalizeBadge(command.BadgeValue);
        var pin = string.IsNullOrWhiteSpace(command.Pin) ? null : command.Pin.Trim();
        var validator = new Validator().Require(TerminalRules.BadgeProblem(value) is null, "BadgeValue", TerminalRules.BadgeProblem(value) ?? "");
        validator.Require(pin is null || TerminalRules.PinProblem(pin) is null, "Pin", pin is null ? "" : TerminalRules.PinProblem(pin) ?? "").ThrowIfInvalid();

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var person = (await labor.GetPersonForUpdateAsync(command.PersonId, cancellationToken))?.Value ?? throw new NotFoundException($"Person {command.PersonId} not found.");
        if (!person.IsActive)
        {
            throw new DomainRuleException($"{person.DisplayName} is switched off.");
        }

        var hash = badgeHasher.Hash(value);
        if (await terminals.FindActiveBadgeForUpdateAsync(hash, cancellationToken) is { } taken && taken.PersonId != person.Id)
        {
            throw new ConflictException("That badge is someone else's; revoke it there first.");
        }

        // A person has one badge: a new one replaces the old.
        if (await terminals.GetActiveBadgeOfPersonForUpdateAsync(person.Id, cancellationToken) is { } old)
        {
            await terminals.UpdateBadgeAsync(old with { RevokedAt = now, RevokedBy = request.ActorId }, cancellationToken);
        }

        var badge = new BadgeRecord(ids.NewId(), person.Id, hash, pin is null ? null : pinHasher.Hash(pin), now, request.ActorId, null, null, 0, null, null);
        await terminals.InsertBadgeAsync(badge, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.BadgeIssued, "person", person.Id, new { BadgeId = badge.Id, HasPin = pin is not null }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(badge.Id, 0);
    }
}

internal sealed class RevokeBadgeHandler(
    IUnitOfWork unitOfWork, ITerminalRepository terminals, IRequestContext request, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<RevokeBadgeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(RevokeBadgeCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var badge = await terminals.GetBadgeForUpdateAsync(command.BadgeId, cancellationToken) ?? throw new NotFoundException($"Badge {command.BadgeId} not found.");
        if (badge.RevokedAt is not null)
        {
            throw new DomainRuleException("That badge is revoked already.");
        }

        await terminals.UpdateBadgeAsync(badge with { RevokedAt = timeProvider.GetUtcNow(), RevokedBy = request.ActorId }, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.BadgeRevoked, "person", badge.PersonId, new { BadgeId = badge.Id }, LaborWriter.Clean(command.Reason)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

internal sealed class IdentifyBadgeHandler(
    IUnitOfWork unitOfWork, ITerminalRepository terminals, ILaborRepository labor, IBadgeHasher badgeHasher, IPasswordHasher pinHasher, IEventLog eventLog,
    IEventFactory events, TimeProvider timeProvider) : ICommandHandler<IdentifyBadgeCommand, BadgeIdentity>
{
    private const string NotRecognised = "Badge not recognised. Try again, or ask a supervisor.";

    public async Task<BadgeIdentity> HandleAsync(IdentifyBadgeCommand command, CancellationToken cancellationToken)
    {
        var value = TerminalRules.NormalizeBadge(command.BadgeValue);
        if (TerminalRules.BadgeProblem(value) is not null)
        {
            throw new DomainRuleException(NotRecognised);
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        await terminals.TouchAsync(command.Terminal.Id, now, cancellationToken);
        var badge = await terminals.FindActiveBadgeForUpdateAsync(badgeHasher.Hash(value), cancellationToken);
        var person = badge is null ? null : (await labor.GetPersonForUpdateAsync(badge.PersonId, cancellationToken))?.Value;
        if (badge is null || person is null || !person.IsActive || person.SiteId != command.Terminal.SiteId)
        {
            await transaction.CommitAsync(cancellationToken);
            throw new DomainRuleException(NotRecognised);
        }

        if (badge.LockedUntil is { } locked && locked > now)
        {
            await transaction.CommitAsync(cancellationToken);
            throw new DomainRuleException($"Too many wrong PINs: this badge is locked until {locked:HH:mm} UTC. Ask a supervisor if you need time recorded now.");
        }

        if (command.Terminal.AuthMode == TerminalAuthModes.BadgePlusPin)
        {
            if (string.IsNullOrEmpty(command.Pin))
            {
                await transaction.CommitAsync(cancellationToken);
                return new BadgeIdentity(NeedsPin: true, null);
            }

            if (badge.PinHash is null || !pinHasher.Verify(command.Pin, badge.PinHash))
            {
                var failed = badge.FailedPins + 1;
                await terminals.UpdateBadgeAsync(badge with { FailedPins = failed, LockedUntil = TerminalRules.LockAfter(failed, now) }, cancellationToken);
                await eventLog.AppendAsync(events.Create(EventTypes.BadgePinFailed, "person", person.Id, new { BadgeId = badge.Id, command.Terminal.Code, Failed = failed }), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw new DomainRuleException(badge.PinHash is null
                    ? "This badge has no PIN yet; ask a supervisor to set one."
                    : failed >= TerminalRules.MaxFailedPins ? "Wrong PIN. The badge is locked for 5 minutes." : $"Wrong PIN. {TerminalRules.MaxFailedPins - failed} tries left.");
            }
        }

        await terminals.UpdateBadgeAsync(badge with { FailedPins = 0, LockedUntil = null, LastUsedAt = now }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BadgeIdentity(NeedsPin: false, person.Id);
    }
}

internal sealed class TerminalClockOnHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IEquipmentRepository equipment, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<TerminalClockOnCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(TerminalClockOnCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var person = await TerminalWriter.PersonAsync(labor, command.Terminal, command.PersonId, cancellationToken);
        if (!await equipment.IsInSubtreeAsync(command.Terminal.ScopeEquipmentId, command.EquipmentId, cancellationToken))
        {
            throw new ForbiddenException($"{command.Terminal.Code} doesn't offer that line.");
        }

        var (session, version, warning) = await LaborWriter.StartAsync(labor, person, new LaborInput(command.EquipmentId, null, LaborTypes.Direct, null, 100, null, null),
            LaborSources.Terminal, command.Terminal.ActorId, timeProvider.GetUtcNow(), ids.NewId(), cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborStarted, "labor_session", session.Id, new { After = session }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(session.Id, version, warning is null ? [] : [warning]);
    }
}

internal sealed class TerminalClockOffHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<TerminalClockOffCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(TerminalClockOffCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        await TerminalWriter.PersonAsync(labor, command.Terminal, command.PersonId, cancellationToken);
        var current = await labor.GetSessionForUpdateAsync(command.SessionId, cancellationToken);
        if (current is null || current.Value.PersonId != command.PersonId)
        {
            throw new ForbiddenException("That isn't your time.");
        }

        if (current.Value.EndedAt is not null)
        {
            throw new DomainRuleException("That time has ended already.");
        }

        if (await labor.IsApprovedAsync(current.Value.Shift, cancellationToken))
        {
            throw new DomainRuleException($"The labor of {LaborWriter.Describe(current.Value.Shift)} is approved already; ask a supervisor.");
        }

        var ended = current.Value with { EndedAt = now };
        var version = await labor.UpdateSessionAsync(ended, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborEnded, "labor_session", ended.Id, new { ended.EndedAt }), cancellationToken);
        await BreakWriter.EndIfOffAsync(labor, eventLog, events, ended.PersonId, now, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(ended.Id, version, []);
    }
}

internal sealed class TerminalStartBreakHandler(
    IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider timeProvider)
    : ICommandHandler<TerminalStartBreakCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(TerminalStartBreakCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var person = await TerminalWriter.PersonAsync(labor, command.Terminal, command.PersonId, cancellationToken);
        var shift = await BreakWriter.CheckAsync(labor, person, now, null, null, now, cancellationToken);
        await BreakWriter.EnsureOpenAsync(labor, shift, cancellationToken);

        var started = new BreakRecord(ids.NewId(), person.SiteId, person.Id, now, null, command.IsPaid, shift.ProductionDate, shift.ShiftScheduleId, shift.ShiftCode,
            LaborSources.Terminal, null, false, null, command.Terminal.ActorId);
        var version = await labor.InsertBreakAsync(started, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborBreakStarted, "labor_break", started.Id, new { After = started }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(started.Id, version, []);
    }
}

internal sealed class TerminalEndBreakHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<TerminalEndBreakCommand, LaborResponse>
{
    public async Task<LaborResponse> HandleAsync(TerminalEndBreakCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        await TerminalWriter.PersonAsync(labor, command.Terminal, command.PersonId, cancellationToken);
        var open = await labor.GetOpenBreakForUpdateAsync(command.PersonId, cancellationToken) ?? throw new DomainRuleException("You aren't on a break.");

        // A break ends after it starts, so one ended in the same instant lasts a second.
        var ended = open.Value with { EndedAt = now > open.Value.StartedAt ? now : open.Value.StartedAt.AddSeconds(1) };
        var version = await labor.UpdateBreakAsync(ended, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.LaborBreakEnded, "labor_break", ended.Id, new { ended.EndedAt }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LaborResponse(ended.Id, version, []);
    }
}

internal sealed class GetTerminalStatusHandler(ITerminalReadStore terminals, ILaborReadStore labor, TimeProvider timeProvider)
    : IQueryHandler<GetTerminalStatusQuery, TerminalStatusDto>
{
    public async Task<TerminalStatusDto> HandleAsync(GetTerminalStatusQuery query, CancellationToken cancellationToken)
    {
        var terminal = query.Terminal;
        var status = new TerminalStatusDto
        {
            TerminalId = terminal.Id,
            TerminalCode = terminal.Code,
            TerminalName = terminal.Name,
            AuthMode = terminal.AuthMode,
            TimeZone = await labor.GetSiteTimeZoneAsync(terminal.SiteId, cancellationToken),
            DefaultEquipmentId = terminal.DefaultEquipmentId,
            Lines = await terminals.ListLinesAsync(terminal.Id, cancellationToken),
        };
        if (query.PersonId is not { } personId)
        {
            return status;
        }

        var now = timeProvider.GetUtcNow();
        var people = await labor.ListPeopleAsync(terminal.SiteId, cancellationToken);
        var open = await labor.ListOpenSessionsAsync(terminal.SiteId, personId, cancellationToken);
        var breaks = await labor.ListBreaksAsync(terminal.SiteId, personId, now.AddHours(-24), now.AddTicks(1), cancellationToken);
        var onBreak = breaks.FirstOrDefault(b => b.Break.EndedAt is null && !b.Break.IsVoid);
        return status with
        {
            PersonId = personId,
            PersonName = people.FirstOrDefault(p => p.Id == personId)?.DisplayName,
            Sessions = [.. open.Select(r => LaborView.ToDto(r, now, LaborView.Counting(breaks)))],
            OpenBreak = onBreak is null ? null : LaborView.ToDto(onBreak, now),
        };
    }
}

internal sealed class ListTerminalsHandler(ITerminalReadStore store) : IQueryHandler<ListTerminalsQuery, IReadOnlyList<TerminalDto>>
{
    public Task<IReadOnlyList<TerminalDto>> HandleAsync(ListTerminalsQuery query, CancellationToken cancellationToken) => store.ListAsync(query.SiteId, cancellationToken);
}

internal sealed class ListBadgesHandler(ITerminalReadStore store) : IQueryHandler<ListBadgesQuery, IReadOnlyList<BadgeDto>>
{
    public Task<IReadOnlyList<BadgeDto>> HandleAsync(ListBadgesQuery query, CancellationToken cancellationToken) => store.ListBadgesAsync(query.SiteId, cancellationToken);
}
