namespace Pulaski.Contracts.Materials;

/// <summary>An external source of material master data that Pulaski pulls from (D-068): an OData v4 service.</summary>
public sealed record MaterialConnectionDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary><c>odata</c>, <c>rest</c>, <c>soap</c> or <c>database</c> (#72).</summary>
    public required string Kind { get; init; }

    /// <summary>The service's address; none for a database.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>OData: the entity set; REST: where the items are in the answer (blank: the answer); SOAP: the element holding one item.</summary>
    public required string EntitySet { get; init; }
    public string? Filter { get; init; }
    public string? NextLinkPath { get; init; }
    public string? RequestBody { get; init; }
    public string? SoapAction { get; init; }
    public string? Query { get; init; }
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

    /// <summary>Fixed once made: <c>odata</c> (default), <c>rest</c>, <c>soap</c> or <c>database</c>.</summary>
    public string Kind { get; init; } = "odata";
    public string? BaseUrl { get; init; }
    public string EntitySet { get; init; } = "";
    public string? Filter { get; init; }
    public string? NextLinkPath { get; init; }
    public string? RequestBody { get; init; }
    public string? SoapAction { get; init; }
    public string? Query { get; init; }
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
