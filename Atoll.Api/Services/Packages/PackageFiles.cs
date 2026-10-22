using System.Text.Json.Serialization;

namespace Atoll.Api.Services.Packages;

/// <summary>
///     Stored file content for the REST surface. <paramref name="Files" /> carries every valid-UTF-8
///     file as text; invalid UTF-8 moves to <paramref name="BinaryFiles" /> as base64 and is omitted
///     from the response entirely when no such file exists, so text-only payloads stay unchanged.
/// </summary>
public record PackageFiles(
    IReadOnlyDictionary<string, string> Files,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, byte[]>? BinaryFiles = null);
