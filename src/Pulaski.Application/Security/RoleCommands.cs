using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Security;

namespace Pulaski.Application.Security;

/// <summary>A new role, held at a site, with the permissions it allows (D-037). Its code is fixed once created.</summary>
public sealed record CreateRoleCommand(string Code, string Name, string? Description, IReadOnlyList<string> Permissions) : ICommand<CreatedResponse>;

/// <summary>Renames a role, switches it on or off and sets all its permissions at once (the Roles screen).</summary>
public sealed record UpdateRoleCommand(string Code, long ExpectedVersion, string Name, string? Description, bool IsActive, IReadOnlyList<string> Permissions)
    : ICommand<VersionResponse>;

/// <summary>Gives a role one permission or takes it away (the Permissions screen). Doing what is already so changes nothing.</summary>
public sealed record SetRolePermissionCommand(string Code, string Permission, bool Granted) : ICommand<VersionResponse>;

internal static class RoleRules
{
    /// <summary>The administrator holds every permission everywhere, so someone can always manage users: it can't be changed.</summary>
    public static void EnsureNotLocked(SecurityRole role)
    {
        if (role.IsGlobal)
        {
            throw new DomainRuleException($"{role.Name} holds every permission and can't be changed.");
        }
    }

    public static string[] EnsurePermissions(IReadOnlyList<string> permissions, string field)
    {
        var unknown = permissions.Where(p => !Permissions.Exists(p)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { [field] = [$"Not a permission: {string.Join(", ", unknown)}."] });
        }

        return [.. permissions.Distinct(StringComparer.Ordinal)];
    }

    public static (string Name, string Description) Text(string name, string? description, Validator validator)
    {
        var trimmedName = name.Trim();
        var trimmedDescription = description?.Trim() ?? "";
        validator
            .RequireText(trimmedName, "Name", 100)
            .Require(trimmedDescription.Length <= 500, "Description", "At most 500 characters.");
        return (trimmedName, trimmedDescription);
    }

    /// <summary>Applies the difference between what the role allows and what it should, one permission at a time.</summary>
    public static async Task SetPermissionsAsync(
        ISecurityRepository security, SecurityRole role, IReadOnlyCollection<string> wanted, DateTimeOffset at, Guid? by, CancellationToken cancellationToken)
    {
        foreach (var permission in wanted.Except(role.Permissions))
        {
            await security.SetRolePermissionAsync(role.Id, permission, granted: true, at, by, cancellationToken);
        }

        foreach (var permission in role.Permissions.Except(wanted))
        {
            await security.SetRolePermissionAsync(role.Id, permission, granted: false, at, by, cancellationToken);
        }
    }
}

internal sealed class CreateRoleHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IRequestContext request,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<CreateRoleCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToLowerInvariant();
        var validator = new Validator();
        if (Roles.CodeProblem(code) is { } problem)
        {
            validator.Require(false, nameof(command.Code), problem);
        }

        var (name, description) = RoleRules.Text(command.Name, command.Description, validator);
        validator.ThrowIfInvalid();
        var permissions = RoleRules.EnsurePermissions(command.Permissions, nameof(command.Permissions));

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (await security.FindRoleForUpdateAsync(code, cancellationToken) is not null)
        {
            throw new ConflictException($"There is already a role {code}.");
        }

        var role = new SecurityRole(ids.NewId(), code, name, description, IsActive: true, []);
        var envelope = events.Create(EventTypes.RoleCreated, "role", role.Id, new { After = role with { Permissions = permissions } });
        var version = await security.InsertRoleAsync(role, envelope.OccurredAt, cancellationToken);
        await RoleRules.SetPermissionsAsync(security, role, permissions, now, request.UserId, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(role.Id, version);
    }
}

internal sealed class UpdateRoleHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IRequestContext request,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<UpdateRoleCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        var validator = new Validator();
        var (name, description) = RoleRules.Text(command.Name, command.Description, validator);
        validator.ThrowIfInvalid();
        var permissions = RoleRules.EnsurePermissions(command.Permissions, nameof(command.Permissions));

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await security.FindRoleForUpdateAsync(command.Code, cancellationToken) ?? throw new NotFoundException($"Role {command.Code} not found.");
        RoleRules.EnsureNotLocked(current.Value);
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Name} was changed by someone else. Reload and try again.");
        }

        // Every user keeps a role (D-037): a role people hold can't be switched off under them.
        if (current.Value.IsActive && !command.IsActive && await security.CountHoldersAsync(current.Value.Id, now, cancellationToken) is var holders and > 0)
        {
            throw new DomainRuleException($"{holders} {(holders == 1 ? "user holds" : "users hold")} {current.Value.Name}; give them another role and take this one away first.");
        }

        var updated = current.Value with { Name = name, Description = description, IsActive = command.IsActive, Permissions = [.. permissions.Order(StringComparer.Ordinal)] };
        if (updated.Name == current.Value.Name && updated.Description == current.Value.Description && updated.IsActive == current.Value.IsActive
            && updated.Permissions.SequenceEqual(current.Value.Permissions.Order(StringComparer.Ordinal)))
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.RoleUpdated, "role", updated.Id, new { Before = current.Value, After = updated });
        await RoleRules.SetPermissionsAsync(security, current.Value, permissions, now, request.UserId, cancellationToken);
        var version = await security.UpdateRoleAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetRolePermissionHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IRequestContext request,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<SetRolePermissionCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetRolePermissionCommand command, CancellationToken cancellationToken)
    {
        RoleRules.EnsurePermissions([command.Permission], nameof(command.Permission));
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var role = await security.FindRoleForUpdateAsync(command.Code, cancellationToken) ?? throw new NotFoundException($"Role {command.Code} not found.");
        RoleRules.EnsureNotLocked(role.Value);
        if (role.Value.Permissions.Contains(command.Permission) == command.Granted)
        {
            return new VersionResponse(role.Version);
        }

        var envelope = events.Create(EventTypes.RolePermissionChanged, "role", role.Value.Id, new { Role = role.Value.Code, command.Permission, command.Granted });
        await security.SetRolePermissionAsync(role.Value.Id, command.Permission, command.Granted, envelope.OccurredAt, request.UserId, cancellationToken);
        var version = await security.UpdateRoleAsync(role.Value, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}
