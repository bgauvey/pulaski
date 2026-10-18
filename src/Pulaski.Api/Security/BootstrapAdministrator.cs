using Microsoft.Extensions.Options;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Security;

/// <summary>
/// At API start (D-037): copies the code's permission catalog into <c>sec.permission</c>, so a role can be given a
/// permission added since the last migration; and when the database has no users at all, creates the first
/// administrator from the <c>Security:BootstrapAdmin</c> settings with a temporary password, to be changed at the first sign-in.
/// </summary>
internal sealed partial class BootstrapAdministrator(IServiceScopeFactory scopes, IOptions<SecurityOptions> options, ILogger<BootstrapAdministrator> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        int users;
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken))
        {
            var security = scope.ServiceProvider.GetRequiredService<ISecurityRepository>();
            await security.SyncPermissionsAsync(Permissions.Catalog, cancellationToken);
            users = await security.CountUsersAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (users > 0)
        {
            return;
        }

        var admin = options.Value.BootstrapAdmin;
        if (string.IsNullOrWhiteSpace(admin.Password))
        {
            LogNoAdministrator(logger);
            return;
        }

        // Checked here so a bad setting says what is wrong rather than failing the start with a bare validation error.
        if (PasswordRules.Problem(admin.Password, admin.Username) is { } problem)
        {
            throw new InvalidOperationException($"Security:BootstrapAdmin:Password isn't acceptable: {problem}");
        }

        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateUserCommand, CreatedResponse>>();
        await handler.HandleAsync(new CreateUserCommand(admin.Username, "Administrator", null, admin.Password, Roles.Administrator, null), cancellationToken);
        LogCreated(logger, admin.Username);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "No users exist and Security:BootstrapAdmin:Password isn't set, so no one can sign in. Set it and restart.")]
    private static partial void LogNoAdministrator(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the first administrator, '{Username}', with a temporary password from Security:BootstrapAdmin.")]
    private static partial void LogCreated(ILogger logger, string username);
}
