using Pulaski.Contracts.Common;

namespace Pulaski.Contracts.Health;

/// <summary>
/// How Pulaski is running (#141, D-118): sections (<c>database</c>, <c>services</c>, <c>edge</c>, <c>deliveries</c>, <c>connections</c>,
/// <c>rollups</c>, <c>mail</c>), each a list of items, worst first. Worked out on request.
/// </summary>
public sealed record SystemHealthDto
{
    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>When this API process started: background services' rounds are known since then.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The worst item's status.</summary>
    public required string Status { get; init; }
    public IReadOnlyList<HealthSectionDto> Sections { get; init; } = [];
}

public sealed record HealthSectionDto
{
    public required string Key { get; init; }
    public required string Status { get; init; }
    public IReadOnlyList<HealthItemDto> Items { get; init; } = [];
}

/// <summary>
/// One thing checked: its name (a code, a feed, a service key), <c>ok</c>, <c>warning</c>, <c>failing</c> or <c>off</c>, what was found, when it
/// last happened, and the screen where it is put right.
/// </summary>
public sealed record HealthItemDto
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required MessageDto Message { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Link { get; init; }
}
