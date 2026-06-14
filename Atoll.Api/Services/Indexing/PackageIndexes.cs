using System.Collections.Immutable;

namespace Atoll.Api.Services.Indexing;

public sealed record PackageIndexes(
    ImmutableDictionary<string, AurPackage> ByNames,
    ImmutableDictionary<string, ImmutableHashSet<string>> ByProvides,
    ImmutableDictionary<string, ImmutableHashSet<string>> ByWords)
{
    public static PackageIndexes Empty { get; } = new(
        ImmutableDictionary<string, AurPackage>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal)
    );
}