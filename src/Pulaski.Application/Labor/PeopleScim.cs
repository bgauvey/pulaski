using System.Text.Json;

namespace Pulaski.Application.Labor;

/// <summary>
/// Reads people from SCIM 2.0 User resources (RFC 7643) into import rows (D-054): a <c>ListResponse</c>, a JSON array of users, or
/// one user. <c>employeeNumber</c> comes from the enterprise extension, the name from <c>displayName</c> (else <c>name.formatted</c>,
/// else given and family name), <c>active</c> as given, and <c>meta.version</c> is kept as the version. Given an external system,
/// every user is that system's person keyed by <c>externalId</c> (else <c>id</c>); without one, they are Pulaski people matched on
/// their employee number. Attribute names are matched without case, as SCIM's are. Other attributes are skipped.
/// </summary>
public static class PeopleScim
{
    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string EnterpriseSchema = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 };

    public static PersonImportSource Parse(string text, string? externalSystem)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PersonImportSource([], ["The file is empty."]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, Options);
        }
        catch (JsonException ex)
        {
            return new PersonImportSource([], [$"The file isn't valid JSON: {ex.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;
            List<JsonElement> users = root.ValueKind switch
            {
                JsonValueKind.Array => [.. root.EnumerateArray()],
                JsonValueKind.Object when Get(root, "Resources") is { ValueKind: JsonValueKind.Array } resources => [.. resources.EnumerateArray()],
                JsonValueKind.Object when Get(root, "userName") is not null || Get(root, "schemas") is not null => [root],
                _ => [],
            };

            if (users.Count == 0)
            {
                return new PersonImportSource([], ["The file has no SCIM users; give a ListResponse with Resources, an array of users, or one user."]);
            }

            if (users.Count > PersonImportFields.MaxRows)
            {
                return new PersonImportSource([], [$"The file has {users.Count} users; import at most {PersonImportFields.MaxRows} at a time."]);
            }

            var system = string.IsNullOrWhiteSpace(externalSystem) ? null : externalSystem.Trim();
            return new PersonImportSource([.. users.Select((u, i) => ToRow(u, i + 1, system))], []);
        }
    }

    private static PersonImportRow ToRow(JsonElement user, int position, string? system)
    {
        var problems = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (user.ValueKind != JsonValueKind.Object)
        {
            return PersonImportFields.ToRow(position, _ => null, ["It isn't a SCIM user (a JSON object)."]);
        }

        if (Get(user, "schemas") is { ValueKind: JsonValueKind.Array } schemas
            && !schemas.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.String && string.Equals(s.GetString(), UserSchema, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("It isn't a SCIM User: its schemas don't include the core User schema.");
        }

        if (Text(Get(Get(user, EnterpriseSchema), "employeeNumber")) is { Length: > 0 } number)
        {
            fields["employee_number"] = number;
        }
        else
        {
            problems.Add("employeeNumber (enterprise extension) is missing; it is the person's employee number.");
        }

        var name = Text(Get(user, "displayName")) is { Length: > 0 } display ? display
            : Text(Get(Get(user, "name"), "formatted")) is { Length: > 0 } formatted ? formatted
            : string.Join(" ", new[] { Text(Get(Get(user, "name"), "givenName")), Text(Get(Get(user, "name"), "familyName")) }.Where(n => !string.IsNullOrEmpty(n)));
        if (name.Length > 0)
        {
            fields["name"] = name;
        }
        else
        {
            problems.Add("displayName and name are missing; one is the person's name.");
        }

        switch (Get(user, "active"))
        {
            case { ValueKind: JsonValueKind.True }:
                fields["active"] = "true";
                break;
            case { ValueKind: JsonValueKind.False }:
                fields["active"] = "false";
                break;
            case { ValueKind: JsonValueKind.String } active:
                fields["active"] = active.GetString()!.Trim();
                break;
        }

        if (system is not null)
        {
            var key = Text(Get(user, "externalId")) is { Length: > 0 } externalId ? externalId : Text(Get(user, "id")) ?? "";
            fields["external_system"] = system;
            fields["external_key"] = key;
            fields["origin"] = PersonOrigins.Hr;
            if (Text(Get(Get(user, "meta"), "version")) is { Length: > 0 } version)
            {
                fields["external_version"] = version;
            }
        }

        return PersonImportFields.ToRow(position, f => fields.GetValueOrDefault(f), problems);
    }

    /// <summary>An attribute of an object, matched without case; null when absent or when <paramref name="parent"/> isn't an object.</summary>
    private static JsonElement? Get(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? Text(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString()!.Trim(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null,
    };
}
