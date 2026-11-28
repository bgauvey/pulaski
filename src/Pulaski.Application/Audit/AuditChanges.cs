using System.Globalization;
using System.Text.Json;
using Pulaski.Contracts.Audit;

namespace Pulaski.Application.Audit;

/// <summary>How an event's data reads in the audit trail (#129): before and after field by field, else its fields. Pure.</summary>
public static class AuditChanges
{
    private const int MaxDepth = 4;
    private static readonly string[] LabelFields = ["code", "number", "lotNumber", "jobNumber", "orderNumber", "username", "name", "displayName", "title"];

    /// <summary>Fields that differ between <c>before</c> and <c>after</c>, in order; empty when the data doesn't carry both.</summary>
    public static IReadOnlyList<AuditFieldChangeDto> Diff(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !TryGet(data, "before", out var before) || !TryGet(data, "after", out var after)
            || before.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null) || after.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            return [];
        }

        var old = Flatten(before);
        var now = Flatten(after);
        return [.. old.Keys.Concat(now.Keys).Distinct(StringComparer.Ordinal)
            .Where(k => old.GetValueOrDefault(k) != now.GetValueOrDefault(k))
            .Select(k => new AuditFieldChangeDto(k, old.GetValueOrDefault(k), now.GetValueOrDefault(k)))];
    }

    /// <summary>The data as fields (nested ones as <c>a.b</c>), for events without before and after; <c>after</c> alone counts as the data.</summary>
    public static IReadOnlyList<AuditFieldDto> Fields(JsonElement data)
    {
        var shown = TryGet(data, "after", out var after) && !TryGet(data, "before", out _) && after.ValueKind == JsonValueKind.Object ? after : data;
        return [.. Flatten(shown).Select(f => new AuditFieldDto(f.Key, f.Value))];
    }

    /// <summary>What the data calls its subject: a code, number or name of the after (else the data itself), if any.</summary>
    public static string? Label(JsonElement data)
    {
        foreach (var source in new[] { TryGet(data, "after", out var a) ? a : default, TryGet(data, "before", out var b) ? b : default, data })
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var field in LabelFields)
            {
                if (TryGet(source, field, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                {
                    return s;
                }
            }
        }

        return null;
    }

    /// <summary>"capability changed" from <c>pulaski.capability.changed.v1</c>; anything else as it is.</summary>
    public static string Action(string type)
    {
        var parts = type.Split('.');
        if (parts.Length < 3 || parts[0] != "pulaski")
        {
            return type;
        }

        var end = parts[^1].StartsWith('v') && int.TryParse(parts[^1][1..], NumberStyles.None, CultureInfo.InvariantCulture, out _) ? parts.Length - 1 : parts.Length;
        var words = string.Join(' ', parts[1..end]).Replace('_', ' ');
        // Recorded event types keep their spelling (a contract); the trail shows the US one.
        return words.Replace("cancelled", "canceled", StringComparison.Ordinal)
            .Replace("signalled", "signaled", StringComparison.Ordinal)
            .Replace("enrolment", "enrollment", StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> Flatten(JsonElement element)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        Walk(element, "", 0, fields);
        return fields;
    }

    private static void Walk(JsonElement element, string path, int depth, Dictionary<string, string?> fields)
    {
        if (element.ValueKind == JsonValueKind.Object && depth < MaxDepth)
        {
            foreach (var p in element.EnumerateObject())
            {
                Walk(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}", depth + 1, fields);
            }

            return;
        }

        if (path.Length > 0)
        {
            fields[path] = element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                // By value: 2.00 and 2 are the same number, whatever scale was stored.
                JsonValueKind.Number when element.TryGetDecimal(out var d) => d.ToString("0.############################", CultureInfo.InvariantCulture),
                JsonValueKind.Array or JsonValueKind.Object => element.GetRawText(),
                _ => element.GetRawText(),
            };
        }
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var p in element.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        return false;
    }
}
