using System.Text.Json.Serialization;

namespace Atoll.Api;

[JsonSerializable(typeof(AurPackage[]))]
[JsonSerializable(typeof(Metrics))]
[JsonSerializable(typeof(QueryType?))]
[JsonSerializable(typeof(QueryValues?))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AppJsonContext : JsonSerializerContext;