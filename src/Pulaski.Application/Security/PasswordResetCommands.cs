using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.Security;

namespace Pulaski.Application.Security;

/// <summary>
/// Someone asks for a link to reset their Pulaski password (D-093), giving their user name or e-mail address. The answer is the same whether or
/// not an account matches; the link goes, later, to the address on file of an active local user who isn't a service account.
/// </summary>
public sealed record RequestPasswordResetCommand(string Login) : ICommand<string>;

/// <summary>Sets a new password with a reset link's token (D-093): once, before it expires.</summary>
public sealed record CompletePasswordResetCommand(string Token, string NewPassword) : ICommand<VersionResponse>;

internal static class PasswordResets
{
    /// <summary>What everyone who asks is told, whether or not an account matches.</summary>
    public const string Answer =
        "If that matches a Pulaski account with an e-mail address, a link to choose a new password is on its way. It works once, for a short time.";

    public const string Expired = "This link has expired or was already used. Ask for a new one on the sign-in page.";

    /// <summary>At most this many links per user in <see cref="Window"/>, and none sooner than <see cref="Gap"/> after the last.</summary>
    public const int MaxPerWindow = 3;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(1);

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static void EnsureEnabled(IPasswordResetSettings settings)
    {
        if (!settings.Enabled)
        {
            throw new NotFoundException("Resetting a password by e-mail isn't switched on here; ask an administrator to reset it.");
        }
    }

    /// <summary>
    /// Who may reset by e-mail: an active local user with an e-mail address, never a machine's account; when only administrators sign in with
    /// a password (D-091), only an administrator.
    /// </summary>
    public static async Task<bool> MayResetAsync(
        ISecurityRepository security, IPasswordResetSettings settings, SecurityUser user, DateTimeOffset now, CancellationToken cancellationToken) =>
        user.IsActive && user.Issuer == Issuers.Local && user.Email is { Length: > 0 } && !user.IsServiceAccount
        && (!settings.AdministratorsOnly
            || (await security.ListActiveAssignmentsForUpdateAsync(user.Id, now, cancellationToken)).Any(a => a.RoleCode == Roles.Administrator));
}

internal sealed class RequestPasswordResetHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IPasswordResetSettings settings,
    IMailOutbox mail,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<RequestPasswordResetCommand, string>
{
    public async Task<string> HandleAsync(RequestPasswordResetCommand command, CancellationToken cancellationToken)
    {
        PasswordResets.EnsureEnabled(settings);
        var login = command.Login?.Trim() ?? "";
        if (login.Length is 0 or > 254)
        {
            return PasswordResets.Answer;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var user = (await security.FindLocalUserForUpdateAsync(SecurityRules.Normalize(login), cancellationToken))?.Value;

        // An address finds someone only when it is one user's: shared ones find no one, the answer the same.
        if (user is null && login.Contains('@', StringComparison.Ordinal)
            && await security.FindLocalUsersByEmailForUpdateAsync(login, cancellationToken) is [var only])
        {
            user = only.Value;
        }

        if (user is null || !await PasswordResets.MayResetAsync(security, settings, user, now, cancellationToken)
            || await security.GetCredentialForUpdateAsync(user.Id, cancellationToken) is null
            || await security.CountPasswordResetsSinceAsync(user.Id, now - PasswordResets.Gap, cancellationToken) > 0
            || await security.CountPasswordResetsSinceAsync(user.Id, now - PasswordResets.Window, cancellationToken) >= PasswordResets.MaxPerWindow)
        {
            return PasswordResets.Answer;
        }

        // A new link ends the earlier ones, so only the latest e-mail works. The token itself is never kept, only its hash.
        var token = PasswordResets.NewToken();
        var link = new PasswordResetLink(ids.NewId(), user.Id, PasswordResets.Hash(token), now, now + settings.LinkLifetime);
        await security.RevokePasswordResetsAsync(user.Id, now, null, cancellationToken);
        await security.InsertPasswordResetAsync(link, cancellationToken);
        await eventLog.AppendAsync(
            events.Create(EventTypes.PasswordResetRequested, "app_user", user.Id, new { user.Username, LinkId = link.Id, link.ExpiresAt }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var minutes = (int)Math.Round(settings.LinkLifetime.TotalMinutes);
        mail.Enqueue(
            user.Email!,
            "Choose a new Pulaski password",
            $"""
            Hello {user.DisplayName},

            Someone asked to reset the Pulaski password of {user.Username}. To choose a new one, open this link within {minutes} minutes:

            {settings.UiBaseUrl.TrimEnd('/')}/account/reset?token={token}

            The link works once. If you didn't ask, ignore this e-mail: your password stays as it is.
            """);
        return PasswordResets.Answer;
    }
}

internal sealed class CompletePasswordResetHandler(
    IUnitOfWork unitOfWork,
    ISecurityRepository security,
    IPasswordResetSettings settings,
    IPasswordHasher hasher,
    IMailOutbox mail,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<CompletePasswordResetCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(CompletePasswordResetCommand command, CancellationToken cancellationToken)
    {
        PasswordResets.EnsureEnabled(settings);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var link = string.IsNullOrWhiteSpace(command.Token) || command.Token.Length > 200
            ? null
            : await security.FindOpenPasswordResetForUpdateAsync(PasswordResets.Hash(command.Token.Trim()), cancellationToken);
        if (link is null || link.ExpiresAt <= now)
        {
            throw new DomainRuleException(PasswordResets.Expired);
        }

        // Switched off, or made a service account, since the link was sent: it no longer works.
        var user = await security.GetUserForUpdateAsync(link.UserId, cancellationToken);
        var credential = user is null ? null : await security.GetCredentialForUpdateAsync(user.Value.Id, cancellationToken);
        if (user is null || credential is null || !await PasswordResets.MayResetAsync(security, settings, user.Value, now, cancellationToken))
        {
            throw new DomainRuleException(PasswordResets.Expired);
        }

        SecurityRules.EnsurePassword(command.NewPassword, user.Value.Username ?? "", nameof(command.NewPassword));
        await security.UpsertCredentialAsync(
            credential with { PasswordHash = hasher.Hash(command.NewPassword), MustChange = false, FailedAttempts = 0, LockedUntil = null }, now, cancellationToken);
        await security.MarkPasswordResetUsedAsync(link.Id, now, cancellationToken);
        await security.RevokePasswordResetsAsync(user.Value.Id, now, link.Id, cancellationToken);
        await eventLog.AppendAsync(
            events.Create(EventTypes.PasswordReset, "app_user", user.Value.Id, new { user.Value.Username, ByEmail = true, LinkId = link.Id }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        mail.Enqueue(
            user.Value.Email!,
            "Your Pulaski password was changed",
            $"""
            Hello {user.Value.DisplayName},

            The Pulaski password of {user.Value.Username} was just changed with a reset link. If that wasn't you, tell an administrator straight away.
            """);
        return new VersionResponse(user.Version);
    }
}

/// <summary>No password reset by e-mail: the default until the host configures it (D-093).</summary>
internal sealed class PasswordResetOff : IPasswordResetSettings
{
    public bool Enabled => false;

    public TimeSpan LinkLifetime => TimeSpan.FromMinutes(30);

    public string UiBaseUrl => "";

    public bool AdministratorsOnly => false;
}

/// <summary>Sends nothing, where no mail server is configured.</summary>
internal sealed class NoMail : IMailOutbox
{
    public bool CanSend => false;

    public void Enqueue(string recipient, string subject, string body)
    {
    }
}
