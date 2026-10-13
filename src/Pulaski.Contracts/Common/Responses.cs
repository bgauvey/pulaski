namespace Pulaski.Contracts.Common;

/// <summary>Returned when a resource is created.</summary>
public sealed record CreatedResponse(Guid Id, long Version);

/// <summary>Returned when a resource is changed; pass the version back on the next change.</summary>
public sealed record VersionResponse(long Version);
