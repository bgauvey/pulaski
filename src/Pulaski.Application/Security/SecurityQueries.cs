using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Application.Security;

/// <summary>The signed-in user with their roles and permissions (D-037).</summary>
public sealed record GetMeQuery(UserAccess Access) : IQuery<MeDto>;

public sealed record ListUsersQuery : IQuery<IReadOnlyList<UserDto>>;

public sealed record GetUserQuery(Guid Id) : IQuery<UserDto>;

public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>;

/// <summary>Every permission the code checks (D-037).</summary>
public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionDto>>;

internal sealed class GetMeHandler(ISecurityReadStore store) : IQueryHandler<GetMeQuery, MeDto>
{
    public async Task<MeDto> HandleAsync(GetMeQuery query, CancellationToken cancellationToken)
    {
        var access = query.Access;
        var sites = await store.GetSiteCodesAsync([.. access.Grants.Where(g => g.SiteId is not null).Select(g => g.SiteId!.Value).Distinct()], cancellationToken);
        return new MeDto
        {
            Id = access.UserId,
            Username = access.Username,
            DisplayName = access.DisplayName,
            ActorId = access.ActorId,
            MustChangePassword = access.MustChangePassword,
            Roles = [.. access.Grants.Select(g => new RoleGrantDto
            {
                AssignmentId = g.AssignmentId,
                RoleCode = g.RoleCode,
                RoleName = g.RoleName,
                SiteId = g.SiteId,
                SiteCode = g.SiteId is { } s ? sites.GetValueOrDefault(s) : null,
            })],
            Permissions = [.. Permissions.All.Keys
                .Where(p => access.Can(p, null))
                .Select(p => new PermissionGrantDto(p, access.SitesFor(p)))],
        };
    }
}

internal sealed class ListUsersHandler(ISecurityReadStore store, TimeProvider timeProvider) : IQueryHandler<ListUsersQuery, IReadOnlyList<UserDto>>
{
    public Task<IReadOnlyList<UserDto>> HandleAsync(ListUsersQuery query, CancellationToken cancellationToken) =>
        store.ListUsersAsync(timeProvider.GetUtcNow(), cancellationToken);
}

internal sealed class GetUserHandler(ISecurityReadStore store, TimeProvider timeProvider) : IQueryHandler<GetUserQuery, UserDto>
{
    public async Task<UserDto> HandleAsync(GetUserQuery query, CancellationToken cancellationToken) =>
        await store.GetUserAsync(query.Id, timeProvider.GetUtcNow(), cancellationToken) ?? throw new NotFoundException($"User {query.Id} not found.");
}

internal sealed class ListRolesHandler(ISecurityReadStore store, TimeProvider timeProvider) : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public Task<IReadOnlyList<RoleDto>> HandleAsync(ListRolesQuery query, CancellationToken cancellationToken) =>
        store.ListRolesAsync(timeProvider.GetUtcNow(), cancellationToken);
}

internal sealed class ListPermissionsHandler : IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionDto>>
{
    public Task<IReadOnlyList<PermissionDto>> HandleAsync(ListPermissionsQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PermissionDto>>([.. Permissions.Catalog.Select(p => new PermissionDto(p.Code, p.Area, p.Description)
        {
            ComesWith = [.. Permissions.Implied.Where(i => i.Value.Contains(p.Code)).Select(i => i.Key)],
        })]);
}
