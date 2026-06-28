using System.Collections.Immutable;

namespace Atoll.Api.Services.Search.Indexing;

public sealed record SearchIndexData(
    ImmutableDictionary<string, AurPackageMetadata> ByNames,
    ImmutableDictionary<string, ImmutableHashSet<string>> ByProvides,
    ImmutableDictionary<string, ImmutableHashSet<string>> ByWords)
{
    public static SearchIndexData Empty { get; } = new(
        ImmutableDictionary<string, AurPackageMetadata>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal)
    );
}