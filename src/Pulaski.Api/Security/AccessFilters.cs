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

/// <summary>
/// Refuses a caller without any of the permissions at the site the request is about (D-037, D-039). A change
/// permission brings the views it needs, so a view check passes for whoever may change the thing too.
/// </summary>
internal sealed class PermissionFilter(IReadOnlyList<string> permissions, SiteResolver? site, bool everywhereWithoutSite = false) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var access = await CallerAccess.RequireAsync(context.HttpContext);
        var siteId = site is null ? null : await site(context);

        // A request for every site's data (no site named) needs the permission everywhere (D-039).
        if (everywhereWithoutSite && siteId is null && access.SitesForAny(permissions) is not null)
        {
            throw new ForbiddenException("Choose a site: you may only see some sites.");
        }

        if (!access.CanAny(permissions, siteId))
        {
            var what = Permissions.All.GetValueOrDefault(permissions[0], permissions[0]);
            var verb = Permissions.Views.Contains(permissions[0]) ? "see this" : "do this";
            throw new ForbiddenException(siteId is null
                ? $"You may not {verb}: it needs permission to {char.ToLowerInvariant(what[0])}{what[1..]}."
                : $"You may not {verb} at this site: it needs permission to {char.ToLowerInvariant(what[0])}{what[1..]}.");
        }

        return await next(context);
    }
}

/// <summary>
/// A back-dated movement (D-057) needs <c>inventory.backdate</c> at the site as well as the movement's own permission; a movement
/// that counts now needs nothing more.
/// </summary>
internal sealed class BackdateFilter(SiteResolver site) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.Arguments.OfType<Pulaski.Contracts.Inventory.IMayBackdate>().FirstOrDefault() is { EffectiveAt: not null })
        {
            var access = await CallerAccess.RequireAsync(context.HttpContext);
            if (!access.CanAny([Permissions.InventoryBackdate], await site(context)))
            {
                throw new ForbiddenException("You may not back-date movements at this site: it needs permission to back-date inventory movements.");
            }
        }

        return await next(context);
    }
}

/// <summary>Lookups many screens need, such as units or the equipment tree (D-039): for whoever may see any screen.</summary>
internal sealed class AnyViewFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!(await CallerAccess.RequireAsync(context.HttpContext)).CanViewAnything)
        {
            throw new ForbiddenException("Your roles don't let you see anything in Pulaski yet; ask an administrator.");
        }

        return await next(context);
    }
}

internal static class PermissionEndpointExtensions
{
    /// <summary>The caller needs <paramref name="permission"/>; at the site <paramref name="site"/> finds, or (without one) at any site.</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission, SiteResolver? site = null) =>
        builder.AddEndpointFilter(new PermissionFilter([permission], site));

    /// <summary>A back-dated request (D-057) also needs <c>inventory.backdate</c> at the site <paramref name="site"/> finds.</summary>
    public static RouteHandlerBuilder RequireToBackdate(this RouteHandlerBuilder builder, SiteResolver site) =>
        builder.AddEndpointFilter(new BackdateFilter(site));

    /// <summary>The caller needs one of <paramref name="permissions"/>, at the site <paramref name="site"/> finds or at any site (D-039).</summary>
    public static RouteHandlerBuilder RequireAny(this RouteHandlerBuilder builder, SiteResolver? site, params string[] permissions) =>
        builder.AddEndpointFilter(new PermissionFilter(permissions, site));

    /// <summary>
    /// Data of one site, named by <paramref name="site"/>: the caller needs one of <paramref name="permissions"/> there. Without a
    /// site the request is for every site, so it needs the permission everywhere (D-039).
    /// </summary>
    public static RouteHandlerBuilder RequireAnyAtSite(this RouteHandlerBuilder builder, SiteResolver site, params string[] permissions) =>
        builder.AddEndpointFilter(new PermissionFilter(permissions, site, everywhereWithoutSite: true));

    /// <summary>A lookup: the caller needs to be able to see some screen (D-039). Lists filter themselves to the caller's sites.</summary>
    public static RouteHandlerBuilder RequireAnyView(this RouteHandlerBuilder builder) => builder.AddEndpointFilter(new AnyViewFilter());
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

    /// <summary>A site named by a query parameter, such as <c>?siteId=</c>; none when it is left out.</summary>
    public static SiteResolver Query(string name = "siteId") => context =>
        ValueTask.FromResult(Guid.TryParse(context.HttpContext.Request.Query[name].ToString(), out var id) ? id : (Guid?)null);

    /// <summary>The site of the thing a query parameter names, such as <c>?lineId=</c>.</summary>
    public static SiteResolver QueryOf(SiteOf kind, string name) => async context =>
        Guid.TryParse(context.HttpContext.Request.Query[name].ToString(), out var id)
            ? await Lookup(context).SiteOfAsync(kind, id, context.HttpContext.RequestAborted)
            : null;

    /// <summary>The site of the thing a request body names, such as the location a lot is received into.</summary>
    public static SiteResolver FromBody<T>(SiteOf kind, Func<T, Guid?> pick) => async context =>
        context.Arguments.OfType<T>().FirstOrDefault() is { } body && pick(body) is { } id
            ? await Lookup(context).SiteOfAsync(kind, id, context.HttpContext.RequestAborted)
            : null;

    private static ISiteLookup Lookup(EndpointFilterInvocationContext context) => context.HttpContext.RequestServices.GetRequiredService<ISiteLookup>();
}

/// <summary>Keeps a list to the sites where the caller may see it (D-039).</summary>
internal static class Visible
{
    /// <summary>The sites where the caller holds any of <paramref name="permissions"/>; null for everywhere.</summary>
    public static async Task<IReadOnlyList<Guid>?> SitesAsync(HttpContext http, params string[] permissions) =>
        (await CallerAccess.RequireAsync(http)).SitesForAny(permissions);

    /// <summary>The sites where the caller may see any screen; null for everywhere.</summary>
    public static async Task<IReadOnlyList<Guid>?> AnyViewSitesAsync(HttpContext http) =>
        (await CallerAccess.RequireAsync(http)).SitesForAny([.. Permissions.Views, .. Permissions.All.Keys]);

    /// <summary>The items at those sites; items that belong to no site (enterprise-wide) stay.</summary>
    public static IReadOnlyList<T> At<T>(this IReadOnlyList<T> items, IReadOnlyList<Guid>? sites, Func<T, Guid?> siteOf) =>
        sites is null ? items : [.. items.Where(i => siteOf(i) is not { } site || sites.Contains(site))];
}
