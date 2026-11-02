using System.Globalization;

namespace Pulaski.Domain.Inventory;

/// <summary>Kinds of container (D-059).</summary>
public static class ContainerKinds
{
    public const string Pallet = "pallet";
    public const string Tote = "tote";
    public const string Box = "box";
    public const string Bin = "bin";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Pallet, Tote, Box, Bin, Other];
}

/// <summary>
/// A pallet, tote, box or bin holding lots at a location in a site (D-059). Its lots keep their own identity; moving it moves them. It may sit
/// inside another container at that one's location (#76): a box on a pallet. Retired, never deleted, and only when empty.
/// </summary>
public sealed record Container(
    Guid Id, Guid SiteId, string Code, string Kind, Guid LocationId, bool IsActive, DateTimeOffset CreatedAt, string CreatedBy, Guid? ParentId = null)
{
    public const int MaxCode = 30;

    /// <summary>How many containers deep nesting may go: a pallet holding cases holding boxes is three.</summary>
    public const int MaxDepth = 5;

    /// <exception cref="DomainRuleException">The kind isn't one of <see cref="ContainerKinds.All"/>.</exception>
    public static Container Create(Guid id, Guid siteId, string code, string kind, Guid locationId, DateTimeOffset at, string by) =>
        ContainerKinds.All.Contains(kind)
            ? new Container(id, siteId, code, kind, locationId, IsActive: true, at, by)
            : throw new DomainRuleException($"'{kind}' isn't a container kind; use {string.Join(", ", ContainerKinds.All)}.");

    /// <summary>Moved on its own: it leaves the container it was in, as a lot moved alone leaves its own.</summary>
    /// <exception cref="DomainRuleException">It is retired, or already there.</exception>
    public Container MoveTo(Guid locationId)
    {
        EnsureActive("moved");
        return locationId == LocationId
            ? throw new DomainRuleException($"{Code} is already there.")
            : this with { LocationId = locationId, ParentId = null };
    }

    /// <summary>
    /// Into <paramref name="outer"/>, at its location (#76). <paramref name="outerAncestors"/> are the containers <paramref name="outer"/> is inside,
    /// and <paramref name="height"/> how many levels this one holds below itself, so neither a cycle nor nesting deeper than <see cref="MaxDepth"/> results.
    /// </summary>
    /// <exception cref="DomainRuleException">Either is retired, they are in different sites, it would go inside itself, or it would nest too deep.</exception>
    public Container PackInto(Container outer, IReadOnlyCollection<Guid> outerAncestors, int height)
    {
        EnsureActive("packed");
        outer.EnsureActive("packed into");
        if (outer.SiteId != SiteId)
        {
            throw new DomainRuleException($"{outer.Code} is at another site than {Code}.");
        }

        if (outer.Id == Id || outerAncestors.Contains(Id))
        {
            throw new DomainRuleException($"{Code} can't go inside {outer.Code}: {outer.Code} is {(outer.Id == Id ? "the same container" : $"inside {Code}")}.");
        }

        if (outer.Id == ParentId)
        {
            throw new DomainRuleException($"{Code} is already inside {outer.Code}.");
        }

        var depth = outerAncestors.Count + 1 + 1 + height;
        return depth > MaxDepth
            ? throw new DomainRuleException($"That would nest containers {depth} deep; at most {MaxDepth}.")
            : this with { ParentId = outer.Id, LocationId = outer.LocationId };
    }

    /// <summary>Out of the container it is inside; it stays where it is.</summary>
    /// <exception cref="DomainRuleException">It isn't inside one, or is retired.</exception>
    public Container TakeOut()
    {
        EnsureActive("taken out");
        return ParentId is null ? throw new DomainRuleException($"{Code} isn't inside another container.") : this with { ParentId = null };
    }

    /// <summary>Carried along when a container it is inside moves.</summary>
    public Container CarriedTo(Guid locationId) => this with { LocationId = locationId };

    /// <exception cref="DomainRuleException">It is retired.</exception>
    public void EnsureActive(string action)
    {
        if (!IsActive)
        {
            throw new DomainRuleException($"{Code} is retired; it can't be {action}.");
        }
    }

    /// <exception cref="DomainRuleException">It still holds stock or containers, or is already retired.</exception>
    public Container Retire(int lotsInside, int containersInside = 0)
    {
        EnsureActive("retired");
        if (containersInside > 0)
        {
            throw new DomainRuleException($"{Code} still holds {containersInside} {(containersInside == 1 ? "container" : "containers")}; take them out first.");
        }

        return lotsInside > 0
            ? throw new DomainRuleException($"{Code} still holds {lotsInside} {(lotsInside == 1 ? "lot" : "lots")}; unpack or move them first.")
            : this with { IsActive = false, ParentId = null };
    }
}

/// <summary>
/// What a scanned code names (D-059). Pulaski's labels carry <c>PUL1L</c> + a lot's label number, <c>PUL1C</c> + a container's, or
/// <c>PUL1E:</c> + a location's code; anything else is free text, such as a lot number on a supplier's label.
/// </summary>
public sealed record ScanCode(string Kind, long? LabelNo, string? Text)
{
    public const string Lot = "lot";
    public const string Container = "container";
    public const string Location = "location";
    public const string FreeText = "text";

    public const string LotPrefix = "PUL1L";
    public const string ContainerPrefix = "PUL1C";
    public const string LocationPrefix = "PUL1E:";

    public static string ForLot(long labelNo) => LotPrefix + labelNo.ToString(CultureInfo.InvariantCulture);

    public static string ForContainer(long labelNo) => ContainerPrefix + labelNo.ToString(CultureInfo.InvariantCulture);

    public static string ForLocation(string code) => LocationPrefix + code;

    /// <summary>The scan, trimmed of what scanners add (spaces, a trailing return). Never throws.</summary>
    public static ScanCode Parse(string scanned)
    {
        var text = scanned.Trim();
        if (Number(text, LotPrefix) is { } lot)
        {
            return new ScanCode(Lot, lot, null);
        }

        if (Number(text, ContainerPrefix) is { } container)
        {
            return new ScanCode(Container, container, null);
        }

        return text.StartsWith(LocationPrefix, StringComparison.OrdinalIgnoreCase) && text.Length > LocationPrefix.Length
            ? new ScanCode(Location, null, text[LocationPrefix.Length..])
            : new ScanCode(FreeText, null, text);
    }

    private static long? Number(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && long.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
}
