using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Labor;

namespace Pulaski.Application.Labor;

/// <summary>A qualification as the commands see it (D-048).</summary>
public sealed record QualificationRecord(Guid Id, Guid SiteId, string Code, string Name, string? Description, int? ValidityMonths, bool IsActive);

/// <summary>A grant of a qualification to a person (D-048).</summary>
public sealed record GrantRecord(
    Guid Id, Guid PersonId, Guid QualificationId, DateOnly ValidFrom, DateOnly? ExpiresOn, string? Note, string GrantedBy,
    DateTimeOffset? RevokedAt, string? RevokedBy, string? RevokeReason);

/// <summary>Write side for qualifications (D-048): runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IQualificationRepository
{
    Task<Versioned<QualificationRecord>?> GetQualificationForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task<long> InsertQualificationAsync(QualificationRecord qualification, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateQualificationAsync(QualificationRecord qualification, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<GrantRecord>?> GetGrantForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertGrantAsync(GrantRecord grant, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateGrantAsync(GrantRecord grant, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Whether the equipment requires the qualification now; null when it never has.</summary>
    Task<bool?> GetRequirementForUpdateAsync(Guid equipmentId, Guid qualificationId, CancellationToken cancellationToken);

    Task<long> SetRequirementAsync(Guid equipmentId, Guid qualificationId, bool isRequired, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>A new qualification at a site (D-048). Its code is fixed once created.</summary>
public sealed record CreateQualificationCommand(Guid SiteId, string Code, string Name, string? Description, int? ValidityMonths) : ICommand<CreatedResponse>;

/// <summary>Changes a qualification; switched off, it can't be granted or required any more, and existing grants stay.</summary>
public sealed record UpdateQualificationCommand(Guid Id, long ExpectedVersion, string Name, string? Description, int? ValidityMonths, bool IsActive)
    : ICommand<VersionResponse>;

/// <summary>Grants a qualification (D-048): from <paramref name="ValidFrom"/> (today) to <paramref name="ExpiresOn"/> (its usual validity). A renewal is a new grant.</summary>
public sealed record GrantQualificationCommand(Guid PersonId, Guid QualificationId, DateOnly? ValidFrom, DateOnly? ExpiresOn, string? Note) : ICommand<CreatedResponse>;

/// <summary>Changes a grant's days or note; a revoked grant can't change.</summary>
public sealed record UpdateQualificationGrantCommand(Guid GrantId, long ExpectedVersion, DateOnly ValidFrom, DateOnly? ExpiresOn, string? Note) : ICommand<VersionResponse>;

/// <summary>Revokes a grant from now, with a reason; it stays for history.</summary>
public sealed record RevokeQualificationGrantCommand(Guid GrantId, long ExpectedVersion, string Reason) : ICommand<VersionResponse>;

/// <summary>Requires a qualification on equipment and everything below it (D-048), or no longer.</summary>
public sealed record SetEquipmentQualificationCommand(Guid EquipmentId, Guid QualificationId, bool IsRequired) : ICommand<VersionResponse>;

internal static class QualificationWriter
{
    public static (string Name, string? Description) Validate(string name, string? description, int? validityMonths)
    {
        var trimmed = name.Trim();
        var text = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        new Validator()
            .RequireText(trimmed, "Name", 100)
            .Require(text is null || text.Length <= 500, "Description", "At most 500 characters.")
            .Require(validityMonths is null or (>= 1 and <= QualificationRules.MaxValidityMonths), "ValidityMonths", "1 to 120 months, or none for no expiry.")
            .ThrowIfInvalid();
        return (trimmed, text);
    }

    public static void ValidateDays(DateOnly validFrom, DateOnly? expiresOn)
    {
        if (QualificationRules.GrantProblem(validFrom, expiresOn) is { } problem)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["ExpiresOn"] = [problem] });
        }
    }

    public static async Task<Versioned<T>> AtVersionAsync<T>(Task<Versioned<T>?> load, long expectedVersion, string what)
    {
        var current = await load ?? throw new NotFoundException($"{what} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"That {what.ToLowerInvariant()} was changed by someone else. Reload and try again.");
        }

        return current;
    }
}

internal sealed class CreateQualificationHandler(
    IUnitOfWork unitOfWork, IQualificationRepository qualifications, ILaborRepository labor, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateQualificationCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateQualificationCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        new Validator().RequireText(code, "Code", 20).ThrowIfInvalid();
        var (name, description) = QualificationWriter.Validate(command.Name, command.Description, command.ValidityMonths);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await labor.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new DomainRuleException("A qualification belongs to a site; choose one.");
        }

        if (await qualifications.CodeExistsAsync(command.SiteId, code, cancellationToken))
        {
            throw new ConflictException($"The site already has a qualification {code}.");
        }

        var created = new QualificationRecord(ids.NewId(), command.SiteId, code, name, description, command.ValidityMonths, IsActive: true);
        var envelope = events.Create(EventTypes.QualificationCreated, "qualification", created.Id, new { After = created });
        var version = await qualifications.InsertQualificationAsync(created, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(created.Id, version);
    }
}

internal sealed class UpdateQualificationHandler(IUnitOfWork unitOfWork, IQualificationRepository qualifications, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateQualificationCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateQualificationCommand command, CancellationToken cancellationToken)
    {
        var (name, description) = QualificationWriter.Validate(command.Name, command.Description, command.ValidityMonths);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await QualificationWriter.AtVersionAsync(
            qualifications.GetQualificationForUpdateAsync(command.Id, cancellationToken), command.ExpectedVersion, "Qualification");
        var updated = current.Value with { Name = name, Description = description, ValidityMonths = command.ValidityMonths, IsActive = command.IsActive };
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.QualificationUpdated, "qualification", updated.Id, new { Before = current.Value, After = updated });
        var version = await qualifications.UpdateQualificationAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class GrantQualificationHandler(
    IUnitOfWork unitOfWork, IQualificationRepository qualifications, ILaborRepository labor, IRequestContext request, IEventLog eventLog,
    IEventFactory events, IIdGenerator ids, TimeProvider timeProvider) : ICommandHandler<GrantQualificationCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(GrantQualificationCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var person = (await labor.GetPersonForUpdateAsync(command.PersonId, cancellationToken))?.Value ?? throw new NotFoundException($"Person {command.PersonId} not found.");
        var qualification = (await qualifications.GetQualificationForUpdateAsync(command.QualificationId, cancellationToken))?.Value
            ?? throw new NotFoundException($"Qualification {command.QualificationId} not found.");
        if (qualification.SiteId != person.SiteId)
        {
            throw new DomainRuleException($"{qualification.Code} belongs to another site than {person.DisplayName}.");
        }

        if (!qualification.IsActive)
        {
            throw new DomainRuleException($"{qualification.Code} is switched off.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(await labor.GetSiteTimeZoneAsync(person.SiteId, cancellationToken) ?? "UTC");
        var from = command.ValidFrom ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        var expires = command.ExpiresOn ?? QualificationRules.DefaultExpiry(from, qualification.ValidityMonths);
        QualificationWriter.ValidateDays(from, expires);

        var grant = new GrantRecord(ids.NewId(), person.Id, qualification.Id, from, expires, LaborWriter.Clean(command.Note), request.ActorId, null, null, null);
        var envelope = events.Create(EventTypes.QualificationGranted, "person", person.Id, new { After = grant });
        var version = await qualifications.InsertGrantAsync(grant, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(grant.Id, version);
    }
}

internal sealed class UpdateQualificationGrantHandler(IUnitOfWork unitOfWork, IQualificationRepository qualifications, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateQualificationGrantCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateQualificationGrantCommand command, CancellationToken cancellationToken)
    {
        QualificationWriter.ValidateDays(command.ValidFrom, command.ExpiresOn);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await QualificationWriter.AtVersionAsync(qualifications.GetGrantForUpdateAsync(command.GrantId, cancellationToken), command.ExpectedVersion, "Grant");
        if (current.Value.RevokedAt is not null)
        {
            throw new DomainRuleException("That grant is revoked; grant the qualification again instead.");
        }

        var updated = current.Value with { ValidFrom = command.ValidFrom, ExpiresOn = command.ExpiresOn, Note = LaborWriter.Clean(command.Note) };
        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.QualificationGrantChanged, "person", updated.PersonId, new { Before = current.Value, After = updated });
        var version = await qualifications.UpdateGrantAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class RevokeQualificationGrantHandler(
    IUnitOfWork unitOfWork, IQualificationRepository qualifications, IRequestContext request, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider)
    : ICommandHandler<RevokeQualificationGrantCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(RevokeQualificationGrantCommand command, CancellationToken cancellationToken)
    {
        var reason = LaborWriter.Clean(command.Reason) ?? throw new ValidationException(new Dictionary<string, string[]> { ["Reason"] = ["Say why it is revoked."] });
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await QualificationWriter.AtVersionAsync(qualifications.GetGrantForUpdateAsync(command.GrantId, cancellationToken), command.ExpectedVersion, "Grant");
        if (current.Value.RevokedAt is not null)
        {
            throw new DomainRuleException("That grant is revoked already.");
        }

        var revoked = current.Value with { RevokedAt = timeProvider.GetUtcNow(), RevokedBy = request.ActorId, RevokeReason = reason };
        var envelope = events.Create(EventTypes.QualificationRevoked, "person", revoked.PersonId, new { Before = current.Value, After = revoked });
        var version = await qualifications.UpdateGrantAsync(revoked, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetEquipmentQualificationHandler(
    IUnitOfWork unitOfWork, IQualificationRepository qualifications, IEquipmentRepository equipment, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetEquipmentQualificationCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetEquipmentQualificationCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var target = (await equipment.GetAsync(command.EquipmentId, RowLock.Share, cancellationToken))?.Value
            ?? throw new NotFoundException($"Equipment {command.EquipmentId} not found.");
        var qualification = (await qualifications.GetQualificationForUpdateAsync(command.QualificationId, cancellationToken))?.Value
            ?? throw new NotFoundException($"Qualification {command.QualificationId} not found.");
        if (target.SiteId != qualification.SiteId)
        {
            throw new DomainRuleException($"{qualification.Code} belongs to another site than {target.Code}.");
        }

        if (command.IsRequired && !qualification.IsActive)
        {
            throw new DomainRuleException($"{qualification.Code} is switched off.");
        }

        var before = await qualifications.GetRequirementForUpdateAsync(target.Id, qualification.Id, cancellationToken);
        if ((before ?? false) == command.IsRequired)
        {
            return new VersionResponse(0);
        }

        var envelope = events.Create(EventTypes.EquipmentQualificationSet, "equipment", target.Id, new
        {
            QualificationId = qualification.Id,
            qualification.Code,
            Before = before ?? false,
            After = command.IsRequired,
        });
        var version = await qualifications.SetRequirementAsync(target.Id, qualification.Id, command.IsRequired, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
