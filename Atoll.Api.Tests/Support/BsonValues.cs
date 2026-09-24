using MongoDB.Bson;

namespace Atoll.Api.Tests.Support;

internal static class BsonValues
{
    public static IEnumerable<BsonValue> Named(BsonValue node, string name)
    {
        switch (node)
        {
            case BsonDocument document:
            {
                foreach (var element in document)
                {
                    if (string.Equals(element.Name, name, StringComparison.Ordinal)) yield return element.Value;
                    foreach (var nested in Named(element.Value, name)) yield return nested;
                }

                break;
            }
            case BsonArray array:
            {
                foreach (var item in array)
                foreach (var nested in Named(item, name)) yield return nested;

                break;
            }
        }
    }
}
