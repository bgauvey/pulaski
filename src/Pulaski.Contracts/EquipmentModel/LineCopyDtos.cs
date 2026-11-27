using Pulaski.Contracts.Common;

namespace Pulaski.Contracts.EquipmentModel;

/// <summary>What copying a line would make (#140, D-132): every piece of equipment with its proposed code and name, and how much comes with it.</summary>
public sealed record LineCopyPlanDto
{
    public required Guid SourceLineId { get; init; }
    public required string SourceCode { get; init; }
    public required Guid SiteId { get; init; }
    public Guid? ParentId { get; init; }

    /// <summary>The site the copy goes to: the original's, or another's (#140c).</summary>
    public Guid TargetSiteId { get; init; }

    /// <summary>Into another site: what the line uses that isn't at that site by its code, and so is left off the copy.</summary>
    public IReadOnlyList<MessageDto> Unmatched { get; init; } = [];
    public IReadOnlyList<LineCopyItemDto> Items { get; init; } = [];
    public int Capabilities { get; init; }
    public int ChangeoverRules { get; init; }
    public int StopCodeOverrides { get; init; }
    public int Qualifications { get; init; }
    public int OeeTargets { get; init; }
    public int MaintenancePlans { get; init; }
    public int InstructionTargets { get; init; }

    /// <summary>Machine tags (edge addresses) on the original: never copied; bind the new machines on Edge nodes.</summary>
    public int TagsLeftOut { get; init; }

    /// <summary>Why it can't be copied like this yet (a code taken or missing).</summary>
    public IReadOnlyList<MessageDto> Problems { get; init; } = [];
}

public sealed record LineCopyItemDto
{
    public required Guid SourceId { get; init; }
    public Guid? SourceParentId { get; init; }
    public int Depth { get; init; }
    public required string SourceCode { get; init; }
    public required string SourceName { get; init; }
    public required string Level { get; init; }
    public required string Role { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
}

/// <summary>Copies a line (#140): the parts wanted, where it goes (the original's parent when left out), and each copy's code and name.</summary>
public sealed record CopyLineRequest
{
    public Guid? ParentId { get; init; }
    public bool Equipment { get; init; } = true;
    public bool Capabilities { get; init; } = true;
    public bool MachineSettings { get; init; } = true;
    public bool Requirements { get; init; } = true;
    public IReadOnlyList<LineCopyNameDto> Items { get; init; } = [];
}

public sealed record LineCopyNameDto(Guid SourceId, string Code, string Name);

/// <summary>What copying a site's reference data would bring (#140b, D-132): how many of each, for the review.</summary>
public sealed record SiteCopyPlanDto
{
    public required Guid SourceSiteId { get; init; }
    public required string SourceCode { get; init; }
    public string? SourceTimeZone { get; init; }
    public int ShiftSchedules { get; init; }
    public int CalendarExceptions { get; init; }
    public int Reasons { get; init; }
    public int MachineProfiles { get; init; }
    public int Crews { get; init; }
    public int Qualifications { get; init; }
    public int AndonTypes { get; init; }
    public int LotAttributes { get; init; }

    /// <summary>The site's own settings rows (changeover mode, tolerances, planning-rate refresh, allocation, release approval) and OEE targets.</summary>
    public int Settings { get; init; }
}

/// <summary>Makes a new site beside the original with the reference data asked for (#140b).</summary>
public sealed record CopySiteRequest
{
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? TimeZone { get; init; }
    public bool Shifts { get; init; } = true;
    public bool Reasons { get; init; } = true;
    public bool People { get; init; } = true;
    public bool Settings { get; init; } = true;
}
