namespace Pulaski.Contracts.Security;

/// <summary>Signs in with a local user name and password (D-037).</summary>
public sealed record SignInRequest
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
}

/// <summary>A signed-in session: the bearer token for the API and who it is for.</summary>
public sealed record SignInResponse
{
    public required string Token { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public required MeDto User { get; init; }
}

/// <summary>The signed-in user, their roles and what they may do (D-037).</summary>
public sealed record MeDto
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>How history names the user, as in "reported by"; their user name for a local user.</summary>
    public string ActorId { get; init; } = "";

    /// <summary>True until a temporary password is replaced; until then only changing it is allowed.</summary>
    public bool MustChangePassword { get; init; }
    public IReadOnlyList<RoleGrantDto> Roles { get; init; } = [];

    /// <summary>Each permission the user holds, with the sites it holds it at; null sites means everywhere.</summary>
    public IReadOnlyList<PermissionGrantDto> Permissions { get; init; } = [];
}

public sealed record RoleGrantDto
{
    public required Guid AssignmentId { get; init; }
    public required string RoleCode { get; init; }
    public required string RoleName { get; init; }

    /// <summary>The site the role is held at; null for a role held everywhere.</summary>
    public Guid? SiteId { get; init; }
    public string? SiteCode { get; init; }
    public DateTimeOffset GrantedAt { get; init; }
}

public sealed record PermissionGrantDto(string Code, IReadOnlyList<Guid>? SiteIds);

public sealed record ChangePasswordRequest
{
    public string CurrentPassword { get; init; } = "";
    public string NewPassword { get; init; } = "";
}

/// <summary>A role and what it may do.</summary>
public sealed record RoleDto
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Held everywhere (the administrator) rather than per site.</summary>
    public bool IsGlobal { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

public sealed record PermissionDto(string Code, string Description);

/// <summary>A user as the Users screen lists them.</summary>
public sealed record UserDto
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Where they sign in: <c>pulaski-local</c>, or an identity provider's issuer.</summary>
    public required string Issuer { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }
    public bool MustChangePassword { get; init; }
    public bool IsLocked { get; init; }
    public IReadOnlyList<RoleGrantDto> Roles { get; init; } = [];
    public long Version { get; init; }
}

/// <summary>A new local user (D-037). Every user belongs to a role, so the first one comes with it.</summary>
public sealed record CreateUserRequest
{
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Email { get; init; }

    /// <summary>A temporary password, to be changed at the first sign-in.</summary>
    public string TemporaryPassword { get; init; } = "";
    public string RoleCode { get; init; } = "";

    /// <summary>The site the role is held at; left out for the administrator.</summary>
    public Guid? SiteId { get; init; }
}

public sealed record UpdateUserRequest
{
    public long Version { get; init; }
    public string DisplayName { get; init; } = "";
    public string? Email { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record ResetPasswordRequest
{
    public string TemporaryPassword { get; init; } = "";
}

public sealed record AssignRoleRequest
{
    public string RoleCode { get; init; } = "";
    public Guid? SiteId { get; init; }
}
