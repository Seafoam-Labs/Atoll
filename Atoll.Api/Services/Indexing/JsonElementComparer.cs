using System.Text.Json;

namespace Atoll.Api.Services.Indexing;

internal sealed class JsonElementComparer : IEqualityComparer<JsonElement>
{
    public static JsonElementComparer Instance { get; } = new();

    public bool Equals(JsonElement x, JsonElement y)
    {
        return JsonElement.DeepEquals(x, y);
    }

    public int GetHashCode(JsonElement obj)
    {
        return obj.ValueKind == JsonValueKind.Undefined
            ? 0
            : obj.GetRawText().GetHashCode(StringComparison.Ordinal);
    }
}