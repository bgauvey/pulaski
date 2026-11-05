namespace Pulaski.Contracts.Materials;

/// <summary>
/// An external source Pulaski pulls master data from (D-068): materials, or since #71 people at a site. The name is kept from when it held only
/// materials.
/// </summary>
public sealed record MaterialConnectionDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary><c>materials</c> or <c>people</c> (#71).</summary>
    public string Target { get; init; } = "materials";

    /// <summary>People: the site they are imported into.</summary>
    public Guid? SiteId { get; init; }

    /// <summary><c>odata</c>, <c>rest</c>, <c>soap</c>, <c>database</c> (#72) or, for people, <c>scim</c> (#71).</summary>
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
    /// <summary>Run every this many hours (#71); null with no <see cref="ScheduleDailyAt"/> = on demand only.</summary>
    public int? ScheduleEveryHours { get; init; }

    /// <summary>Or daily at these UTC times, up to four.</summary>
    public IReadOnlyList<TimeOnly> ScheduleDailyAt { get; init; } = [];

    /// <summary>When the schedule runs it next; null when it isn't scheduled (or is switched off).</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Materials: create classes the items name that don't exist yet (D-055).</summary>
    public bool CreateClasses { get; init; }

    /// <summary>People: switch off the system's people at the site that the source no longer has (#71).</summary>
    public bool SwitchOffMissing { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastRunBy { get; init; }
    public string? LastRunSummary { get; init; }

    /// <summary><c>applied</c>, <c>not_applied</c> (errors, nothing saved) or <c>failed</c> (the source couldn't be read).</summary>
    public string? LastRunOutcome { get; init; }
    public long Version { get; init; }
}

/// <summary>One import a connection ran (#71), on demand or by its schedule. Checks aren't recorded.</summary>
public sealed record ConnectionRunDto
{
    public required Guid Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }

    /// <summary><c>manual</c> or <c>schedule</c>.</summary>
    public required string Trigger { get; init; }

    /// <summary><c>applied</c>, <c>not_applied</c> or <c>failed</c>.</summary>
    public required string Outcome { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Errors { get; init; }
    public required string Summary { get; init; }
    public required string Actor { get; init; }
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

    /// <summary>People connections: the site, fixed once made (#71). Ignored for materials.</summary>
    public Guid? SiteId { get; init; }

    /// <summary>Every 1–168 hours, or null (#71).</summary>
    public int? ScheduleEveryHours { get; init; }

    /// <summary>Or daily at up to four UTC times; not both.</summary>
    public IReadOnlyList<TimeOnly> ScheduleDailyAt { get; init; } = [];
    public bool CreateClasses { get; init; }
    public bool SwitchOffMissing { get; init; }
}

/// <summary>Pull now: <see cref="DryRun"/> checks and saves nothing; otherwise all or nothing, as a file import.</summary>
public sealed record RunMaterialConnectionRequest
{
    public bool DryRun { get; init; } = true;
    public bool CreateClasses { get; init; }
}

/// <summary>Pull a people connection now (#71): <see cref="DryRun"/> checks and saves nothing; otherwise all or nothing, as a file import.</summary>
public sealed record RunPeopleConnectionRequest
{
    public bool DryRun { get; init; } = true;
}
