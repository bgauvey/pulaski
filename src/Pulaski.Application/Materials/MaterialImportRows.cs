using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Materials;

/// <summary>
/// One item for a bulk import (D-034), whatever it came from: a CSV file today, an adapter for
/// another external source later. Code, name, type and base unit are always given; the other
/// fields are null when the source left them out. With an external system and key the row is the
/// ERP's item and is matched on them; without, it is a Pulaski material matched on its code.
/// </summary>
public sealed record MaterialImportRow
{
    /// <summary>Where the row is in its source, for messages: the line of a file.</summary>
    public int Line { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string MaterialType { get; init; } = "";
    public string BaseUomCode { get; init; } = "";
    public string? ExternalSystem { get; init; }
    public string? ExternalKey { get; init; }
    public string? ExternalVersion { get; init; }

    /// <summary>A class code; a blank value removes the class.</summary>
    public Supplied<string?>? ClassCode { get; init; }
    public Supplied<string?>? Description { get; init; }
    public Supplied<bool>? IsLotTracked { get; init; }
    public Supplied<int?>? ShelfLifeDays { get; init; }
    public Supplied<bool>? IsActive { get; init; }

    /// <summary>What the source couldn't read in this row, such as "lot_tracked: 'maybe' isn't yes or no"; the row is then an error.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>Rows from a source, and what was wrong with the source as a whole (a missing or unknown column).</summary>
public sealed record MaterialImportSource(IReadOnlyList<MaterialImportRow> Rows, IReadOnlyList<string> Problems);
