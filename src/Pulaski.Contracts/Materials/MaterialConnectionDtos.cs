namespace Pulaski.Contracts.Materials;

/// <summary>An external source of material master data that Pulaski pulls from (D-068): an OData v4 service.</summary>
public sealed record MaterialConnectionDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary><c>odata</c>: an OData v4 service.</summary>
    public required string Kind { get; init; }
    public required string BaseUrl { get; init; }
    public required string EntitySet { get; init; }
    public string? Filter { get; init; }
    public required string ExternalSystem { get; init; }

    /// <summary>Pulaski's import field (code, name, base_unit, external_key…) → the service's property name, or a path such as <c>Unit/Code</c>.</summary>
    public IReadOnlyDictionary<string, string> FieldMap { get; init; } = new Dictionary<string, string>();
    public bool IsActive { get; init; }

    /// <summary>Whether the API's configuration has credentials for it (<c>MaterialConnections:&lt;code&gt;</c>); never the credentials.</summary>
    public bool HasCredentials { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastRunBy { get; init; }
    public string? LastRunSummary { get; init; }
    public long Version { get; init; }
}

public sealed record SaveMaterialConnectionRequest
{
    public long Version { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string EntitySet { get; init; } = "";
    public string? Filter { get; init; }
    public string ExternalSystem { get; init; } = "";
    public IReadOnlyDictionary<string, string> FieldMap { get; init; } = new Dictionary<string, string>();
    public bool IsActive { get; init; } = true;
}

/// <summary>Pull now: <see cref="DryRun"/> checks and saves nothing; otherwise all or nothing, as a file import.</summary>
public sealed record RunMaterialConnectionRequest
{
    public bool DryRun { get; init; } = true;
    public bool CreateClasses { get; init; }
}
