using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>A new machine profile at a site (D-050), starting with PackML's own state numbers. Its code is fixed once created.</summary>
public sealed record CreateMachineProfileCommand(Guid SiteId, string Code, string Name, string? Vendor, string? Model, string? Description)
    : ICommand<CreatedResponse>;

public sealed record UpdateMachineProfileCommand(Guid Id, long ExpectedVersion, string Name, string? Vendor, string? Model, string? Description, bool IsActive)
    : ICommand<VersionResponse>;

/// <summary>Adds or changes the state value <paramref name="RawValue"/> of a profile.</summary>
public sealed record SetProfileStateCommand(Guid ProfileId, long ExpectedVersion, int RawValue, string Name, int PackMlState, Guid? ReasonCodeId, bool IsActive)
    : ICommand<VersionResponse>;

/// <summary>Adds or changes a stop, alarm or warning code of a profile.</summary>
public sealed record SetProfileCodeCommand(
    Guid ProfileId, long ExpectedVersion, string Kind, int RawValue, string Message, string? Category, Guid? ReasonCodeId, bool IsActive)
    : ICommand<VersionResponse>;

/// <summary>The utilization reasons operators may choose on the profile's machines, besides those its codes map to.</summary>
public sealed record SetProfileReasonsCommand(Guid ProfileId, long ExpectedVersion, IReadOnlyList<Guid> ReasonCodeIds) : ICommand<VersionResponse>;

internal static class ProfileRules
{
    public const int NameLength = 100;
    public const int MessageLength = 200;
    public const int CategoryLength = 50;

    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static void Validate(Validator validator, string name, string? vendor, string? model, string? description) => validator
        .RequireText(name, "Name", NameLength)
        .Require(vendor is null || vendor.Length <= NameLength, "Vendor", $"At most {NameLength} characters.")
        .Require(model is null || model.Length <= NameLength, "Model", $"At most {NameLength} characters.")
        .Require(description is null || description.Length <= 500, "Description", "At most 500 characters.")
        .ThrowIfInvalid();

    /// <summary>The profile, locked, at the version the caller read.</summary>
    public static async Task<MachineProfileRecord> LockAsync(IMachineDataRepository machines, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await machines.GetProfileForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Machine profile {id} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }

    /// <summary>The reason, when given, is an active utilization reason for every site or for <paramref name="siteId"/>.</summary>
    public static async Task EnsureReasonAsync(IMachineDataRepository machines, Guid siteId, Guid? reasonCodeId, CancellationToken cancellationToken)
    {
        if (reasonCodeId is { } id && (await machines.ListUtilizationReasonsAsync(siteId, cancellationToken)).All(r => r.Id != id))
        {
            throw new DomainRuleException("Choose an active utilization reason of the profile's site.");
        }
    }
}

internal sealed class CreateMachineProfileHandler(IUnitOfWork unitOfWork, IMachineDataRepository machines, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<CreateMachineProfileCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateMachineProfileCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        var (vendor, model, description) = (ProfileRules.Optional(command.Vendor), ProfileRules.Optional(command.Model), ProfileRules.Optional(command.Description));
        var validator = new Validator().Require(MachineProfileRules.IsValidCode(code), "Code", "Letters, digits, '-', '_' or '.', up to 20.");
        ProfileRules.Validate(validator, name, vendor, model, description);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await machines.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new DomainRuleException("A machine profile belongs to a site; choose one.");
        }

        if (await machines.ProfileCodeExistsAsync(command.SiteId, code, cancellationToken))
        {
            throw new ConflictException($"The site already has a machine profile {code}.");
        }

        var profile = new MachineProfileRecord(ids.NewId(), command.SiteId, code, name, vendor, model, description, IsActive: true);
        var envelope = events.Create(EventTypes.MachineProfileCreated, "machine_profile", profile.Id, new { After = profile });
        await machines.InsertProfileAsync(profile, envelope.OccurredAt, cancellationToken);
        foreach (var (raw, stateName, packml) in MachineProfileRules.StandardStates)
        {
            await machines.UpsertStateAsync(new ProfileStateRecord(profile.Id, raw, stateName, packml, ReasonCodeId: null, IsActive: true), cancellationToken);
        }

        // Writing the states doesn't change the profile row, so its version is read back after them.
        var version = await machines.UpdateProfileAsync(profile, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(profile.Id, version);
    }
}

internal sealed class UpdateMachineProfileHandler(IUnitOfWork unitOfWork, IMachineDataRepository machines, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<UpdateMachineProfileCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateMachineProfileCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        var (vendor, model, description) = (ProfileRules.Optional(command.Vendor), ProfileRules.Optional(command.Model), ProfileRules.Optional(command.Description));
        ProfileRules.Validate(new Validator(), name, vendor, model, description);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await ProfileRules.LockAsync(machines, command.Id, command.ExpectedVersion, cancellationToken);
        var updated = current with { Name = name, Vendor = vendor, Model = model, Description = description, IsActive = command.IsActive };
        if (updated == current)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        if (current.IsActive && !updated.IsActive && await machines.ListActiveMachinesAsync(current.Id, cancellationToken) is { Count: > 0 } users)
        {
            throw new DomainRuleException($"{current.Code} is still used by {string.Join(", ", users)}; give them another profile first.");
        }

        var envelope = events.Create(EventTypes.MachineProfileUpdated, "machine_profile", updated.Id, new { Before = current, After = updated });
        var version = await machines.UpdateProfileAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetProfileStateHandler(IUnitOfWork unitOfWork, IMachineDataRepository machines, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetProfileStateCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetProfileStateCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        new Validator()
            .RequireText(name, "Name", ProfileRules.NameLength)
            .Require(PackMlStates.IsValid(command.PackMlState), "PackMlState", "A PackML state, 0 to 17.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var profile = await ProfileRules.LockAsync(machines, command.ProfileId, command.ExpectedVersion, cancellationToken);
        var before = (await machines.ListStatesAsync(profile.Id, cancellationToken)).FirstOrDefault(s => s.RawValue == command.RawValue);
        var after = new ProfileStateRecord(profile.Id, command.RawValue, name, command.PackMlState, command.ReasonCodeId, command.IsActive);
        if (after == before)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        if (command.ReasonCodeId != before?.ReasonCodeId)
        {
            await ProfileRules.EnsureReasonAsync(machines, profile.SiteId, command.ReasonCodeId, cancellationToken);
        }

        var envelope = events.Create(EventTypes.MachineProfileStateSet, "machine_profile", profile.Id, new { Before = before, After = after });
        await machines.UpsertStateAsync(after, cancellationToken);
        var version = await machines.UpdateProfileAsync(profile, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetProfileCodeHandler(IUnitOfWork unitOfWork, IMachineDataRepository machines, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetProfileCodeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetProfileCodeCommand command, CancellationToken cancellationToken)
    {
        var message = command.Message.Trim();
        var category = ProfileRules.Optional(command.Category);
        new Validator()
            .Require(StopCodeKinds.All.Contains(command.Kind), "Kind", "A stop reason, alarm or warning.")
            .RequireText(message, "Message", ProfileRules.MessageLength)
            .Require(category is null || category.Length <= ProfileRules.CategoryLength, "Category", $"At most {ProfileRules.CategoryLength} characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var profile = await ProfileRules.LockAsync(machines, command.ProfileId, command.ExpectedVersion, cancellationToken);
        var before = (await machines.ListCodesAsync(profile.Id, cancellationToken)).FirstOrDefault(c => c.Kind == command.Kind && c.RawValue == command.RawValue);
        var after = new ProfileCodeRecord(profile.Id, command.Kind, command.RawValue, message, category, command.ReasonCodeId, command.IsActive);
        if (after == before)
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        if (command.ReasonCodeId != before?.ReasonCodeId)
        {
            await ProfileRules.EnsureReasonAsync(machines, profile.SiteId, command.ReasonCodeId, cancellationToken);
        }

        var envelope = events.Create(EventTypes.MachineProfileCodeSet, "machine_profile", profile.Id, new { Before = before, After = after });
        await machines.UpsertCodeAsync(after, cancellationToken);
        var version = await machines.UpdateProfileAsync(profile, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetProfileReasonsHandler(IUnitOfWork unitOfWork, IMachineDataRepository machines, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetProfileReasonsCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetProfileReasonsCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var profile = await ProfileRules.LockAsync(machines, command.ProfileId, command.ExpectedVersion, cancellationToken);
        var wanted = command.ReasonCodeIds.ToHashSet();
        var offered = (await machines.ListOfferedAsync(profile.Id, cancellationToken)).ToHashSet();
        if (wanted.SetEquals(offered))
        {
            return new VersionResponse(command.ExpectedVersion);
        }

        // A reason switched off since it was ticked may stay ticked; a newly ticked one must be usable.
        var usable = (await machines.ListUtilizationReasonsAsync(profile.SiteId, cancellationToken)).Select(r => r.Id).ToHashSet();
        if (wanted.Except(offered).Any(id => !usable.Contains(id)))
        {
            throw new DomainRuleException("Offer only active utilization reasons of the profile's site.");
        }

        foreach (var id in wanted.Except(offered))
        {
            await machines.UpsertOfferedAsync(profile.Id, id, isOffered: true, cancellationToken);
        }

        foreach (var id in offered.Except(wanted))
        {
            await machines.UpsertOfferedAsync(profile.Id, id, isOffered: false, cancellationToken);
        }

        var envelope = events.Create(EventTypes.MachineProfileReasonsSet, "machine_profile", profile.Id, new { Before = offered, After = wanted });
        var version = await machines.UpdateProfileAsync(profile, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
