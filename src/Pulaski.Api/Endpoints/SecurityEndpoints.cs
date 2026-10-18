using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Signing in, the signed-in user, and users and their roles (D-037).</summary>
internal static class SecurityEndpoints
{
    public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder routes)
    {
        var auth = routes.MapGroup("/auth").WithTags("Security");

        auth.MapPost("/sign-in", [AllowSignedOut] async Task<Ok<SignInResponse>> (
            SignInRequest request,
            ICommandHandler<SignInCommand, SignedInUser> handler,
            IQueryHandler<GetMeQuery, MeDto> me,
            ISecurityReadStore store,
            TokenIssuer issuer,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var user = await handler.HandleAsync(new SignInCommand(request.Username, request.Password), ct);
            var (token, expires) = issuer.Issue(user);
            var access = await store.GetAccessAsync(user.Issuer, user.Subject, timeProvider.GetUtcNow(), ct);
            return TypedResults.Ok(new SignInResponse { Token = token, ExpiresAt = expires, User = await me.HandleAsync(new GetMeQuery(access!), ct) });
        });

        auth.MapPost("/password", [AllowPasswordChange] async Task<Ok<VersionResponse>> (
            ChangePasswordRequest request,
            HttpContext http,
            ICommandHandler<ChangePasswordCommand, VersionResponse> handler,
            CancellationToken ct) =>
        {
            var access = await CallerAccess.RequireAsync(http, allowPasswordChange: true);
            return TypedResults.Ok(await handler.HandleAsync(new ChangePasswordCommand(access.UserId, request.CurrentPassword, request.NewPassword), ct));
        });

        routes.MapGet("/me", [AllowPasswordChange] async Task<Ok<MeDto>> (HttpContext http, IQueryHandler<GetMeQuery, MeDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMeQuery(await CallerAccess.RequireAsync(http, allowPasswordChange: true)), ct)))
            .WithTags("Security");

        // Roles are data an administrator maintains; permissions are the code's (D-037).
        var roles = routes.MapGroup("/roles").WithTags("Security");

        roles.MapGet("/", async Task<Ok<IReadOnlyList<RoleDto>>> (IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListRolesQuery(), ct)));

        roles.MapPost("/", async Task<Created<CreatedResponse>> (CreateRoleRequest request, ICommandHandler<CreateRoleCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateRoleCommand(request.Code, request.Name, request.Description, request.Permissions), ct);
            return TypedResults.Created($"/api/roles/{request.Code.Trim().ToLowerInvariant()}", created);
        }).RequirePermission(Permissions.Security);

        roles.MapPut("/{code}", async Task<Ok<VersionResponse>> (string code, UpdateRoleRequest request, ICommandHandler<UpdateRoleCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateRoleCommand(code, request.Version, request.Name, request.Description, request.IsActive, request.Permissions), ct)))
            .RequirePermission(Permissions.Security);

        roles.MapPut("/{code}/permissions/{permission}", async Task<Ok<VersionResponse>> (
            string code, string permission, SetRolePermissionRequest request, ICommandHandler<SetRolePermissionCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetRolePermissionCommand(code, permission, request.Granted), ct)))
            .RequirePermission(Permissions.Security);

        routes.MapGet("/permissions", async Task<Ok<IReadOnlyList<PermissionDto>>> (IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListPermissionsQuery(), ct)))
            .WithTags("Security");

        var users = routes.MapGroup("/users").WithTags("Security");

        users.MapGet("/", async Task<Ok<IReadOnlyList<UserDto>>> (IQueryHandler<ListUsersQuery, IReadOnlyList<UserDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListUsersQuery(), ct)))
            .RequirePermission(Permissions.Security);

        users.MapGet("/{id:guid}", async Task<Ok<UserDto>> (Guid id, IQueryHandler<GetUserQuery, UserDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetUserQuery(id), ct)))
            .RequirePermission(Permissions.Security);

        users.MapPost("/", async Task<Created<CreatedResponse>> (CreateUserRequest request, ICommandHandler<CreateUserCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateUserCommand(request.Username, request.DisplayName, request.Email, request.TemporaryPassword, request.RoleCode, request.SiteId), ct);
            return TypedResults.Created($"/api/users/{created.Id}", created);
        }).RequirePermission(Permissions.Security);

        users.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (Guid id, UpdateUserRequest request, ICommandHandler<UpdateUserCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateUserCommand(id, request.Version, request.DisplayName, request.Email, request.IsActive), ct)))
            .RequirePermission(Permissions.Security);

        users.MapPost("/{id:guid}/password", async Task<Ok<VersionResponse>> (Guid id, ResetPasswordRequest request, ICommandHandler<ResetPasswordCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ResetPasswordCommand(id, request.TemporaryPassword), ct)))
            .RequirePermission(Permissions.Security);

        users.MapPost("/{id:guid}/roles", async Task<Created<CreatedResponse>> (Guid id, AssignRoleRequest request, ICommandHandler<AssignRoleCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new AssignRoleCommand(id, request.RoleCode, request.SiteId), ct);
            return TypedResults.Created($"/api/users/{id}", created);
        }).RequirePermission(Permissions.Security);

        users.MapPost("/roles/{assignmentId:guid}/end", async Task<Ok<VersionResponse>> (Guid assignmentId, ICommandHandler<EndRoleCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EndRoleCommand(assignmentId), ct)))
            .RequirePermission(Permissions.Security);

        return routes;
    }
}
