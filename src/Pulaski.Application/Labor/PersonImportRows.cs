using Pulaski.Application.Abstractions;

namespace Pulaski.Application.Labor;

/// <summary>
/// One person from an import source (D-054), before any rule is applied. Source-neutral: a CSV file, a SCIM document or any
/// later adapter produces these, and <see cref="ImportPeopleCommand"/> applies the rules. A field the source didn't supply is null
/// and keeps what the person has.
/// </summary>
public sealed record PersonImportRow
{
    /// <summary>Where the row is in its source: a CSV line, or a SCIM resource's position.</summary>
    public int Line { get; init; }
    public string EmployeeNumber { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary><c>hr</c> or <c>erp</c>, for a person from an external system; null to keep theirs (<c>hr</c> for someone new).</summary>
    public string? Origin { get; init; }
    public string? ExternalSystem { get; init; }
    public string? ExternalKey { get; init; }
    public string? ExternalVersion { get; init; }
    public Supplied<bool>? IsActive { get; init; }

    /// <summary>A crew code at the site; a blank value takes them off their crew. Pulaski's field, applied because a Pulaski user loads the file.</summary>
    public Supplied<string?>? CrewCode { get; init; }

    /// <summary>The Pulaski user they sign in as; a blank value unlinks them. Pulaski's field, as the crew.</summary>
    public Supplied<string?>? Username { get; init; }

    /// <summary>Problems reading the row's values, such as a yes/no that is neither.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>The rows a source produced, and any problem with the source as a whole (then there are no rows).</summary>
public sealed record PersonImportSource(IReadOnlyList<PersonImportRow> Rows, IReadOnlyList<string> Problems);

/// <summary>
/// The fields a person import row is read from (D-054), by canonical name, and how their text becomes a <see cref="PersonImportRow"/>;
/// every source adapter goes through <see cref="ToRow"/>.
/// </summary>
public static class PersonImportFields
{
    /// <summary>The most rows one import takes, whatever its source.</summary>
    public const int MaxRows = 5000;

    /// <summary>The fields, by their canonical name, with the other names accepted for them.</summary>
    public static IReadOnlyDictionary<string, string[]> Names { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["employee_number"] = ["employee_no", "employee", "emp_no"],
        ["name"] = ["display_name", "full_name", "person_name"],
        ["active"] = ["is_active"],
        ["crew"] = ["crew_code"],
        ["username"] = ["user", "user_name"],
        ["origin"] = [],
        ["external_system"] = [],
        ["external_key"] = [],
        ["external_version"] = [],
    };

    /// <summary>The canonical name for <paramref name="name"/> (already normalized), or null when it names no field.</summary>
    public static string? Canonical(string name) =>
        Names.ContainsKey(name) ? name : Names.FirstOrDefault(c => c.Value.Contains(name)).Key;

    /// <summary>
    /// One row from its fields. <paramref name="field"/> gives a field's trimmed text by canonical name: null when the source doesn't
    /// supply it (the person keeps what they have), empty when it supplies a blank.
    /// </summary>
    public static PersonImportRow ToRow(int line, Func<string, string?> field, IEnumerable<string>? problems = null)
    {
        var rowProblems = new List<string>(problems ?? []);
        string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

        Supplied<bool>? active = null;
        if (field("active") is { Length: > 0 } value)
        {
            switch (value.ToLowerInvariant())
            {
                case "true" or "yes" or "y" or "1":
                    active = new(true);
                    break;
                case "false" or "no" or "n" or "0":
                    active = new(false);
                    break;
                default:
                    rowProblems.Add($"active: '{value}' isn't yes or no.");
                    break;
            }
        }

        return new PersonImportRow
        {
            Line = line,
            EmployeeNumber = field("employee_number") ?? "",
            DisplayName = field("name") ?? "",
            Origin = Blank(field("origin"))?.ToLowerInvariant(),
            ExternalSystem = Blank(field("external_system")),
            ExternalKey = Blank(field("external_key")),
            ExternalVersion = Blank(field("external_version")),
            IsActive = active,
            CrewCode = field("crew") is { } crew ? new(Blank(crew)) : null,
            Username = field("username") is { } user ? new(Blank(user)) : null,
            Problems = rowProblems,
        };
    }
}
