using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Security;

namespace Pulaski.Application.Security;

/// <summary>Who signed in, for the API to issue a token to (D-037).</summary>
public sealed record SignedInUser(Guid UserId, string Issuer, string Subject, string Username, string DisplayName, bool MustChangePassword);

/// <summary>Checks a local user's password (D-037). Five failures in a row lock the user for 15 minutes.</summary>
public sealed record SignInCommand(string Username, string Password) : ICommand<SignedInUser>;

/// <summary>The signed-in user replaces their password, also a temporary one.</summary>
public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword) : ICommand<VersionResponse>;

/// <summary>A new local user with their first role: every user belongs to a role.</summary>
public sealed record CreateUserCommand(string Username, string DisplayName, string? Email, string TemporaryPassword, string RoleCode, Guid? SiteId)
    : ICommand<CreatedResponse>;

public sealed record UpdateUserCommand(Guid Id, long ExpectedVersion, string DisplayName, string? Email, bool IsActive) : ICommand<VersionResponse>;

/// <summary>Gives a local user a new temporary password and unlocks them.</summary>
public sealed record ResetPasswordCommand(Guid Id, string TemporaryPassword) : ICommand<VersionResponse>;

public sealed record AssignRoleCommand(Guid UserId, string RoleCode, Guid? SiteId) : ICommand<CreatedResponse>;

/// <summary>Takes a role away. Assignments are ended, never deleted, so history keeps who could do what.</summary>
public sealed record EndRoleCommand(Guid AssignmentId) : ICommand<VersionResponse>;

internal static class SecurityRules
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    public static void EnsurePassword(string password, string username, string field)
    {
        if (PasswordRules.Problem(password, username) is { } problem)
        {
            throw new ValidationException(new Dictionary<string, string[]> { [field] = [problem] });
        }
    }

    /// <summary>The role exists, and is held everywhere (administrator) or at a site.</summary>
    public static async Task<Role> EnsureRoleAsync(ISecurityRepository security, string roleCode, Guid? siteId, CancellationToken cancellationToken)
    {
        var role = Roles.Find(roleCode) ?? throw new DomainRuleException($"'{roleCode}' isn't a role.");
        if (role.IsGlobal && siteId is not null)
        {
            throw new DomainRuleException($"{role.Name} is held everywhere, not at a site.");
        }

        if (!role.IsGlobal && (siteId is not { } site || !await security.IsSiteAsync(site, cancellationToken)))
        {
            throw new DomainRuleException($"{role.Name} is held at a site; choose one.");
        }

        return role;
    }

    /// <summary>Someone must always be able to manage users: the last active administrator can't lose the role or be switched off.</summary>
    public static async Task EnsureNotLastAdministratorAsync(ISecurityRepository security, Guid userId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (await security.CountOtherAdministratorsAsync(userId, at, cancellationToken) == 0)
        {
            throw new DomainRuleException("This is the last active administrator; make someone else an administrator first.");
        }
    }
}

internal sealed class SignInHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IPasswordHasher hasher,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<SignInCommand, SignedInUser>
{
    private const string Refused = "The user name or password is wrong, or the user is locked or switched off.";

    public async Task<SignedInUser> HandleAsync(SignInCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var user = await security.FindLocalUserForUpdateAsync(SecurityRules.Normalize(command.Username), cancellationToken);
        var credential = user is null ? null : await security.GetCredentialForUpdateAsync(user.Value.Id, cancellationToken);
        if (user is null || credential is null)
        {
            throw new UnauthorizedException(Refused);
        }

        if (credential.LockedUntil > now || !user.Value.IsActive)
        {
            throw new UnauthorizedException(Refused);
        }

        if (!hasher.Verify(command.Password, credential.PasswordHash))
        {
            var failures = credential.FailedAttempts + 1;
            var locked = failures >= SecurityRules.MaxFailures ? now + SecurityRules.Lockout : (DateTimeOffset?)null;
            await security.UpsertCredentialAsync(credential with { FailedAttempts = locked is null ? failures : 0, LockedUntil = locked }, now, cancellationToken);
            await eventLog.AppendAsync(
                events.Create(EventTypes.SignInFailed, "app_user", user.Value.Id, new { Failures = failures, LockedUntil = locked }), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedException(Refused);
        }

        // Every user belongs to a role (D-037): without one there is nothing they may do.
        if ((await security.ListActiveAssignmentsForUpdateAsync(user.Value.Id, now, cancellationToken)).Count == 0)
        {
            throw new UnauthorizedException($"{user.Value.DisplayName} has no role yet; ask an administrator to give you one.");
        }

        await security.UpsertCredentialAsync(credential with { FailedAttempts = 0, LockedUntil = null }, now, cancellationToken);
        await security.SetLastLoginAsync(user.Value.Id, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.SignedIn, "app_user", user.Value.Id, new { user.Value.Username }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SignedInUser(user.Value.Id, user.Value.Issuer, user.Value.Subject, user.Value.Username!, user.Value.DisplayName, credential.MustChange);
    }
}

internal sealed class ChangePasswordHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IPasswordHasher hasher,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<ChangePasswordCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var user = await security.GetUserForUpdateAsync(command.UserId, cancellationToken) ?? throw new NotFoundException("User not found.");
        var credential = await security.GetCredentialForUpdateAsync(user.Value.Id, cancellationToken)
            ?? throw new DomainRuleException($"{user.Value.DisplayName} signs in through {user.Value.Issuer}; change the password there.");
        if (!hasher.Verify(command.CurrentPassword, credential.PasswordHash))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["CurrentPassword"] = ["That isn't the current password."] });
        }

        SecurityRules.EnsurePassword(command.NewPassword, user.Value.Username ?? "", "NewPassword");
        if (hasher.Verify(command.NewPassword, credential.PasswordHash))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["NewPassword"] = ["Choose a password you haven't just used."] });
        }

        var now = timeProvider.GetUtcNow();
        await security.UpsertCredentialAsync(credential with { PasswordHash = hasher.Hash(command.NewPassword), MustChange = false, FailedAttempts = 0, LockedUntil = null }, now, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.PasswordChanged, "app_user", user.Value.Id, new { user.Value.Username }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(user.Version);
    }
}

internal sealed class CreateUserHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IPasswordHasher hasher,
    IRequestContext request,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<CreateUserCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateUserCommand command, CancellationToken cancellationToken)
    {
        var username = SecurityRules.Normalize(command.Username);
        var displayName = command.DisplayName.Trim();
        var email = string.IsNullOrWhiteSpace(command.Email) ? null : command.Email.Trim();
        new Validator()
            .RequireText(username, nameof(command.Username), 100)
            .Require(username.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@'), nameof(command.Username), "Letters, digits and . - _ @ only.")
            .RequireText(displayName, nameof(command.DisplayName), 200)
            .Require(email is null || (email.Length <= 254 && email.Contains('@', StringComparison.Ordinal)), nameof(command.Email), "An e-mail address, or blank.")
            .RequireText(command.RoleCode, nameof(command.RoleCode), 50)
            .ThrowIfInvalid();
        SecurityRules.EnsurePassword(command.TemporaryPassword, username, nameof(command.TemporaryPassword));

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (await security.FindLocalUserForUpdateAsync(username, cancellationToken) is not null)
        {
            throw new ConflictException($"There is already a user {username}.");
        }

        var role = await SecurityRules.EnsureRoleAsync(security, command.RoleCode, command.SiteId, cancellationToken);
        var user = new SecurityUser(ids.NewId(), Issuers.Local, username, username, displayName, email, IsActive: true);
        var envelope = events.Create(EventTypes.UserCreated, "app_user", user.Id, new { After = user, Role = role.Code, command.SiteId });
        var version = await security.InsertUserAsync(user, envelope.OccurredAt, cancellationToken);
        await security.UpsertCredentialAsync(new LocalCredential(user.Id, hasher.Hash(command.TemporaryPassword), MustChange: true, 0, null), now, cancellationToken);
        await security.InsertAssignmentAsync(new RoleAssignment(ids.NewId(), user.Id, role.Code, command.SiteId, now, null), request.UserId, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(user.Id, version);
    }
}

internal sealed class UpdateUserHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IRequestContext request,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<UpdateUserCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateUserCommand command, CancellationToken cancellationToken)
    {
        var displayName = command.DisplayName.Trim();
        var email = string.IsNullOrWhiteSpace(command.Email) ? null : command.Email.Trim();
        new Validator()
            .RequireText(displayName, nameof(command.DisplayName), 200)
            .Require(email is null || (email.Length <= 254 && email.Contains('@', StringComparison.Ordinal)), nameof(command.Email), "An e-mail address, or blank.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await security.GetUserForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"User {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.DisplayName} was changed by someone else. Reload and try again.");
        }

        var updated = current.Value with { DisplayName = displayName, Email = email, IsActive = command.IsActive };
        if (current.Value.IsActive && !updated.IsActive)
        {
            if (request.UserId == updated.Id)
            {
                throw new DomainRuleException("You can't switch yourself off.");
            }

            var now = timeProvider.GetUtcNow();
            if ((await security.ListActiveAssignmentsForUpdateAsync(updated.Id, now, cancellationToken)).Any(a => a.RoleCode == Roles.Administrator))
            {
                await SecurityRules.EnsureNotLastAdministratorAsync(security, updated.Id, now, cancellationToken);
            }
        }

        if (updated == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        var envelope = events.Create(EventTypes.UserUpdated, "app_user", updated.Id, new { Before = current.Value, After = updated });
        var version = await security.UpdateUserAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class ResetPasswordHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IPasswordHasher hasher,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<ResetPasswordCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var user = await security.GetUserForUpdateAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"User {command.Id} not found.");
        if (user.Value.Issuer != Issuers.Local)
        {
            throw new DomainRuleException($"{user.Value.DisplayName} signs in through {user.Value.Issuer}; reset the password there.");
        }

        SecurityRules.EnsurePassword(command.TemporaryPassword, user.Value.Username ?? "", nameof(command.TemporaryPassword));
        await security.UpsertCredentialAsync(new LocalCredential(user.Value.Id, hasher.Hash(command.TemporaryPassword), MustChange: true, 0, null), timeProvider.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.PasswordReset, "app_user", user.Value.Id, new { user.Value.Username }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(user.Version);
    }
}

internal sealed class AssignRoleHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IRequestContext request,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<AssignRoleCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(AssignRoleCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var user = await security.GetUserForUpdateAsync(command.UserId, cancellationToken) ?? throw new NotFoundException($"User {command.UserId} not found.");
        var role = await SecurityRules.EnsureRoleAsync(security, command.RoleCode, command.SiteId, cancellationToken);
        var active = await security.ListActiveAssignmentsForUpdateAsync(user.Value.Id, now, cancellationToken);
        if (active.Any(a => a.RoleCode == role.Code && a.SiteId == command.SiteId))
        {
            throw new DomainRuleException($"{user.Value.DisplayName} already holds {role.Name} there.");
        }

        var assignment = new RoleAssignment(ids.NewId(), user.Value.Id, role.Code, command.SiteId, now, null);
        await security.InsertAssignmentAsync(assignment, request.UserId, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.RoleAssigned, "app_user", user.Value.Id, new { AssignmentId = assignment.Id, Role = role.Code, command.SiteId }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(assignment.Id, user.Version);
    }
}

internal sealed class EndRoleHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IRequestContext request,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<EndRoleCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(EndRoleCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var peek = await security.GetAssignmentForUpdateAsync(command.AssignmentId, cancellationToken) ?? throw new NotFoundException("Role assignment not found.");
        var user = await security.GetUserForUpdateAsync(peek.UserId, cancellationToken) ?? throw new NotFoundException("User not found.");
        var active = await security.ListActiveAssignmentsForUpdateAsync(user.Value.Id, now, cancellationToken);
        var assignment = active.FirstOrDefault(a => a.Id == command.AssignmentId) ?? throw new DomainRuleException("That role has already been taken away.");

        // Every user belongs to a role (D-037): to stop someone using Pulaski, switch them off instead.
        if (user.Value.IsActive && active.Count == 1)
        {
            throw new DomainRuleException($"That is {user.Value.DisplayName}'s only role, and every user needs one. Give them another first, or switch them off.");
        }

        if (assignment.RoleCode == Roles.Administrator)
        {
            await SecurityRules.EnsureNotLastAdministratorAsync(security, user.Value.Id, now, cancellationToken);
        }

        await security.EndAssignmentAsync(assignment.Id, now, request.UserId, cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.RoleEnded, "app_user", user.Value.Id, new { AssignmentId = assignment.Id, Role = assignment.RoleCode, assignment.SiteId }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(user.Version);
    }
}
