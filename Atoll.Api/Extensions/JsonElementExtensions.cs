using System.Text.Json;

namespace Atoll.Api.Extensions;

public static class JsonElementExtensions
{
    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static long GetInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return 0;

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt64(out var n) => n,
            JsonValueKind.Number when prop.TryGetDouble(out var d) => (long)d,
            _ => 0
        };
    }

    private static long? GetInt64OrNull(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return null;

        return prop.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when prop.TryGetInt64(out var n) => n,
            JsonValueKind.Number when prop.TryGetDouble(out var d) => (long)d,
            _ => null
        };
    }

    private static double GetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop)) return 0.0;

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetDouble(out var d) => d,
            JsonValueKind.Number when prop.TryGetInt64(out var n) => n,
            _ => 0.0
        };
    }

    extension(JsonElement element)
    {
        public AurPackage DeserializeAurPackage()
        {
            return new AurPackage(
                GetInt64(element, "ID"),
                GetString(element, "Name") ?? string.Empty,
                GetInt64(element, "PackageBaseID"),
                GetString(element, "PackageBase") ?? string.Empty,
                GetString(element, "Version") ?? string.Empty,
                GetString(element, "Description") ?? string.Empty,
                GetString(element, "URL"),
                GetInt64(element, "NumVotes"),
                GetDouble(element, "Popularity"),
                GetInt64OrNull(element, "OutOfDate"),
                GetString(element, "Maintainer"),
                GetString(element, "Submitter"),
                GetInt64(element, "FirstSubmitted"),
                GetInt64(element, "LastModified"),
                GetString(element, "URLPath") ?? string.Empty,
                element.TryGetStringArray("Depends"),
                element.TryGetStringArray("MakeDepends"),
                element.TryGetStringArray("OptDepends"),
                element.TryGetStringArray("Conflicts"),
                element.TryGetStringArray("Provides"),
                element.TryGetStringArray("License"),
                element.TryGetStringArray("Keywords"),
                element.TryGetStringArray("CoMaintainers")
            );
        }

        public string[] TryGetStringArray(string property)
        {
            if (!element.TryGetProperty(property, out var propertyElement) ||
                propertyElement.ValueKind != JsonValueKind.Array) return [];

            return propertyElement
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        }
    }
}