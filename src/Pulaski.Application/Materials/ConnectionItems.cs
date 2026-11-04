using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Pulaski.Application.Materials;

/// <summary>
/// Turns what a REST, SOAP or database connection answers into items (#72): JSON objects whose properties the field map reads, the same as an
/// OData item (D-068), so every kind goes through <see cref="ODataMaterialRows.Read"/> and on to the one import (D-053). Pure; nothing here
/// decides anything about materials.
/// </summary>
public static class ConnectionItems
{
    private const int MaxDepth = 5;

    // No DTDs, so no entity expansion and nothing fetched from elsewhere.
    private static readonly XmlReaderSettings Xml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>
    /// The items of a REST answer: the array at <paramref name="path"/> (parts split by '.' or '/'), or the answer itself when the path is blank
    /// and it is an array. Null with why when there is no such array.
    /// </summary>
    public static (IReadOnlyList<JsonElement>? Items, string? Problem) Rest(JsonElement answer, string path)
    {
        var found = path.Length == 0 ? answer : Find(answer, path);
        return found is { ValueKind: JsonValueKind.Array } array
            ? ([.. array.EnumerateArray().Select(e => e.Clone())], null)
            : (null, path.Length == 0 ? "the answer isn't an array; give the path to the items." : $"the answer has no array at '{path}'.");
    }

    /// <summary>The next page's address in a REST answer, when <paramref name="path"/> names one and it is a non-empty string.</summary>
    public static string? NextLink(JsonElement answer, string? path) =>
        path is null ? null : Find(answer, path) is { ValueKind: JsonValueKind.String } link && link.GetString() is { Length: > 0 } href ? href : null;

    /// <summary>
    /// The items of a SOAP answer: every element named <paramref name="element"/> (local name, any namespace), each as an object of its child
    /// elements (nested ones as objects, text as strings, empty as null). A SOAP fault is a problem, with its message.
    /// </summary>
    public static (IReadOnlyList<JsonElement>? Items, string? Problem) Soap(string xml, string element)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Xml);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            return (null, $"the answer isn't well-formed XML: {ex.Message}");
        }

        if (document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault") is { } fault)
        {
            var text = fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultstring" or "Text" or "Reason")?.Value.Trim();
            return (null, $"the service answered with a SOAP fault{(string.IsNullOrEmpty(text) ? "" : $": {text}")}.");
        }

        return ([.. document.Descendants().Where(e => e.Name.LocalName == element).Select(ToJson)], null);
    }

    /// <summary>One database row as an item: each column by its name, numbers and booleans as such, times as ISO 8601, the rest as text.</summary>
    public static JsonElement Row(IReadOnlyList<(string Name, object? Value)> columns)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in columns)
            {
                writer.WritePropertyName(name);
                switch (value)
                {
                    case null or DBNull:
                        writer.WriteNullValue();
                        break;
                    case bool b:
                        writer.WriteBooleanValue(b);
                        break;
                    case decimal d:
                        writer.WriteNumberValue(d);
                        break;
                    case int or long or short or byte:
                        writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                        break;
                    case double or float:
                        writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                        break;
                    case DateTime dt:
                        writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                        break;
                    case DateTimeOffset dto:
                        writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                        break;
                    case DateOnly date:
                        writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        break;
                    default:
                        writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement? Find(JsonElement answer, string path)
    {
        var current = answer;
        foreach (var part in path.Split('.', '/'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static JsonElement ToJson(XElement element)
    {
        var json = new StringBuilder();
        Write(element, json, 0);
        using var document = JsonDocument.Parse(json.ToString());
        return document.RootElement.Clone();
    }

    // An element with children is an object (the first of repeated names wins); a leaf is its text, or null when empty.
    private static void Write(XElement element, StringBuilder json, int depth)
    {
        var children = element.Elements().ToList();
        if (children.Count == 0 || depth >= MaxDepth)
        {
            var text = element.Value.Trim();
            json.Append(text.Length == 0 ? "null" : JsonSerializer.Serialize(text));
            return;
        }

        json.Append('{');
        var first = true;
        foreach (var child in children.GroupBy(c => c.Name.LocalName).Select(g => g.First()))
        {
            json.Append(first ? "" : ",").Append(JsonSerializer.Serialize(child.Name.LocalName)).Append(':');
            Write(child, json, depth + 1);
            first = false;
        }

        json.Append('}');
    }
}
