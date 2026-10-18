using Pulaski.Application.Errors;
using Pulaski.Application.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Security;

/// <summary>The signed-in caller's access, resolved once per request (D-037).</summary>
internal static class CallerAccess
{
    private const string Key = "pulaski.access";

    /// <summary>Who is calling and what they may do; null when no one has signed in or Pulaski doesn't know them.</summary>
    public static async Task<UserAccess?> GetAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(Key, out var cached))
        {
            return cached as UserAccess;
        }

        UserAccess? access = null;
        var issuer = http.User.FindFirst("iss")?.Value;
        var subject = http.User.FindFirst("sub")?.Value;
        if (http.User.Identity?.IsAuthenticated == true && issuer is not null && subject is not null)
        {
            var store = http.RequestServices.GetRequiredService<ISecurityReadStore>();
            var clock = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            access = await store.GetAccessAsync(issuer, subject, clock.GetUtcNow(), http.RequestAborted);
        }

        http.Items[Key] = access;
        return access;
    }

    /// <summary>The caller, who must be an active user with a role, and done with any temporary password.</summary>
    public static async Task<UserAccess> RequireAsync(HttpContext http, bool allowPasswordChange = false)
    {
        var access = await GetAsync(http) ?? throw new UnauthorizedException("Sign in first.");
        if (!access.IsActive)
        {
            throw new UnauthorizedException($"{access.DisplayName} is switched off.");
        }

        if (!access.HasAnyRole)
        {
            throw new ForbiddenException($"{access.DisplayName} has no role; ask an administrator to give you one.");
        }

        if (access.MustChangePassword && !allowPasswordChange)
        {
            throw new ForbiddenException("Change your temporary password first.");
        }

        return access;
    }
}

/// <summary>Every API call but signing in needs a signed-in user with a role (D-037): viewing is open to all of them.</summary>
internal sealed class SignedInFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var endpoint = context.HttpContext.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AllowSignedOutAttribute>() is null)
        {
            await CallerAccess.RequireAsync(context.HttpContext, endpoint?.Metadata.GetMetadata<AllowPasswordChangeAttribute>() is not null);
        }

        return await next(context);
    }
}

/// <summary>Marks an endpoint that works without signing in: signing in itself.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
internal sealed class AllowSignedOutAttribute : Attribute;

/// <summary>Marks an endpoint a user with a temporary password may still call.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
internal sealed class AllowPasswordChangeAttribute : Attribute;

/// <summary>Where a permission is checked: a site found from the request, or none (something enterprise-wide).</summary>
internal delegate ValueTask<Guid?> SiteResolver(EndpointFilterInvocationContext context);

/// <summary>Refuses a caller without the permission at the site the request is about (D-037).</summary>
internal sealed class PermissionFilter(string permission, SiteResolver? site) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var access = await CallerAccess.RequireAsync(context.HttpContext);
        var siteId = site is null ? null : await site(context);
        if (!access.Can(permission, siteId))
        {
            var what = Permissions.All.GetValueOrDefault(permission, permission);
            throw new ForbiddenException(siteId is null
                ? $"You may not do this: it needs permission to {char.ToLowerInvariant(what[0])}{what[1..]}."
                : $"You may not do this at this site: it needs permission to {char.ToLowerInvariant(what[0])}{what[1..]}.");
        }

        return await next(context);
    }
}

internal static class PermissionEndpointExtensions
{
    /// <summary>The caller needs <paramref name="permission"/>; at the site <paramref name="site"/> finds, or (without one) at any site.</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission, SiteResolver? site = null) =>
        builder.AddEndpointFilter(new PermissionFilter(permission, site));
}

/// <summary>Finds the site a request is about, for <see cref="PermissionFilter"/>.</summary>
internal static class Site
{
    /// <summary>
    /// The site of the thing named by a route value, such as <c>{id}</c>. Something that belongs to no site (an
    /// enterprise-wide process or reason) or doesn't exist gives none; the handler reports a missing one.
    /// </summary>
    public static SiteResolver Of(SiteOf kind, string routeValue = "id") => async context =>
        Guid.TryParse(context.HttpContext.Request.RouteValues[routeValue]?.ToString(), out var id)
            ? await Lookup(context).SiteOfAsync(kind, id, context.HttpContext.RequestAborted)
            : null;

    /// <summary>A site named directly by a route value, such as <c>{siteId}</c>.</summary>
    public static SiteResolver Route(string routeValue = "siteId") => context =>
        ValueTask.FromResult(Guid.TryParse(context.HttpContext.Request.RouteValues[routeValue]?.ToString(), out var id) ? id : (Guid?)null);

    /// <summary>The site of the thing a request body names, such as the location a lot is received into.</summary>
    public static SiteResolver FromBody<T>(SiteOf kind, Func<T, Guid?> pick) => async context =>
        context.Arguments.OfType<T>().FirstOrDefault() is { } body && pick(body) is { } id
            ? await Lookup(context).SiteOfAsync(kind, id, context.HttpContext.RequestAborted)
            : null;

    private static ISiteLookup Lookup(EndpointFilterInvocationContext context) => context.HttpContext.RequestServices.GetRequiredService<ISiteLookup>();
}
