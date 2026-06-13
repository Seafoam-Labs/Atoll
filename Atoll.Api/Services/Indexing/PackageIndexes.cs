using System.Collections.Immutable;
using System.Text.Json;

namespace Atoll.Api.Services.Indexing;

public sealed record PackageIndexes(
    ImmutableDictionary<string, JsonElement> ByNames,
    ImmutableDictionary<string, ImmutableHashSet<string>> ByProvides,
    ImmutableDictionary<string, ImmutableHashSet<string>> ByWords)
{
    public static PackageIndexes Empty { get; } = new(
        ImmutableDictionary<string, JsonElement>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal)
    );
}