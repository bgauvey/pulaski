using System.Text.Json;

namespace Pulaski.Infrastructure.Processes;

/// <summary>
/// The stored shape of a process definition (<c>core.process_version.definition</c>): camelCase,
/// the same as the API's, so a domain definition and its DTO read and write the same document.
/// </summary>
internal static class ProcessJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("A process definition was empty.");
}
