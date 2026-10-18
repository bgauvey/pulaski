using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;

namespace Pulaski.Application.Labor;

/// <summary>A new crew at a site (D-040). Its code is fixed once created, because shift schedules name it.</summary>
public sealed record CreateCrewCommand(Guid SiteId, string Code, string Name) : ICommand<CreatedResponse>;

public sealed record UpdateCrewCommand(Guid Id, long ExpectedVersion, string Name, bool IsActive) : ICommand<VersionResponse>;

/// <summary>Someone whose time is recorded (D-040), optionally the Pulaski user they sign in as.</summary>
public sealed record CreatePersonCommand(Guid SiteId, string EmployeeNumber, string DisplayName, Guid? CrewId, string? Username) : ICommand<CreatedResponse>;

public sealed record UpdatePersonCommand(Guid Id, long ExpectedVersion, string DisplayName, Guid? CrewId, string? Username, bool IsActive) : ICommand<VersionResponse>;

internal static class PeopleRules
{
    /// <summary>The crew, when given, is an active crew of the person's site.</summary>
    public static async Task EnsureCrewAsync(ILaborRepository labor, Guid? crewId, Guid siteId, CancellationToken cancellationToken)
    {
        if (crewId is not { } id)
        {
            return;
        }

        var crew = (await labor.GetCrewForUpdateAsync(id, cancellationToken))?.Value;
        if (crew is null || crew.SiteId != siteId || !crew.IsActive)
        {
            throw new DomainRuleException("Choose an active crew of the person's site.");
        }
    }

    /// <summary>The user the person signs in as: a local user no other person is linked to.</summary>
    public static async Task<Guid?> UserAsync(ILaborRepository labor, string? username, Guid personId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var userId = await labor.FindLocalUserIdAsync(username.Trim().ToLowerInvariant(), cancellationToken)
            ?? throw new DomainRuleException($"There is no user {username.Trim()}.");
        if (await labor.UserIsAnotherPersonAsync(userId, personId, cancellationToken))
        {
            throw new DomainRuleException($"{username.Trim()} is already linked to another person.");
        }

        return userId;
    }
}

internal sealed class CreateCrewHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateCrewCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateCrewCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        new Validator().RequireText(code, "Code", 20).RequireText(name, "Name", 100).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await labor.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new DomainRuleException("A crew belongs to a site; choose one.");
        }

        if (await labor.CrewCodeExistsAsync(command.SiteId, code, cancellationToken))
        {
            throw new ConflictException($"The site already has a crew {code}.");
        }

        var crew = new CrewRecord(ids.NewId(), command.SiteId, code, name, IsActive: true);
        var envelope = events.Create(EventTypes.CrewCreated, "crew", crew.Id, new { After = crew });
        var version = await labor.InsertCrewAsync(crew, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(crew.Id, version);
    }
}

internal sealed class UpdateCrewHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateCrewCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateCrewCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        new Validator().RequireText(name, "Name", 100).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await labor.GetCrewForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Crew {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var updated = current.Value with { Name = name, IsActive = command.IsActive };
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.CrewUpdated, "crew", updated.Id, new { Before = current.Value, After = updated });
        var version = await labor.UpdateCrewAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class CreatePersonHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreatePersonCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreatePersonCommand command, CancellationToken cancellationToken)
    {
        var number = command.EmployeeNumber.Trim();
        var name = command.DisplayName.Trim();
        new Validator().RequireText(number, "EmployeeNumber", 50).RequireText(name, "DisplayName", 200).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await labor.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new DomainRuleException("A person belongs to a site; choose one.");
        }

        if (await labor.EmployeeNumberExistsAsync(command.SiteId, number, cancellationToken))
        {
            throw new ConflictException($"The site already has employee number {number}.");
        }

        var id = ids.NewId();
        await PeopleRules.EnsureCrewAsync(labor, command.CrewId, command.SiteId, cancellationToken);
        var person = new PersonRecord(id, command.SiteId, number, name, command.CrewId, await PeopleRules.UserAsync(labor, command.Username, id, cancellationToken), IsActive: true);
        var envelope = events.Create(EventTypes.PersonCreated, "person", person.Id, new { After = person });
        var version = await labor.InsertPersonAsync(person, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(person.Id, version);
    }
}

internal sealed class UpdatePersonHandler(IUnitOfWork unitOfWork, ILaborRepository labor, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdatePersonCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdatePersonCommand command, CancellationToken cancellationToken)
    {
        var name = command.DisplayName.Trim();
        new Validator().RequireText(name, "DisplayName", 200).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await labor.GetPersonForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Person {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.DisplayName} was changed by someone else. Reload and try again.");
        }

        if (command.CrewId != current.Value.CrewId)
        {
            await PeopleRules.EnsureCrewAsync(labor, command.CrewId, current.Value.SiteId, cancellationToken);
        }

        var updated = current.Value with
        {
            DisplayName = name,
            CrewId = command.CrewId,
            AppUserId = await PeopleRules.UserAsync(labor, command.Username, current.Value.Id, cancellationToken),
            IsActive = command.IsActive,
        };

        // Someone who leaves stops their time: they can't be switched off while on a line.
        if (current.Value.IsActive && !updated.IsActive && (await labor.ListOpenSessionsOfPersonForUpdateAsync(updated.Id, cancellationToken)).Count > 0)
        {
            throw new DomainRuleException($"{name} is still clocked on; end their time first.");
        }

        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.PersonUpdated, "person", updated.Id, new { Before = current.Value, After = updated });
        var version = await labor.UpdatePersonAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
