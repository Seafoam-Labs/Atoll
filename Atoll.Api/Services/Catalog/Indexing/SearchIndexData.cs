using System.Collections.Immutable;

namespace Atoll.Api.Services.Catalog.Indexing;

/// <summary>
///     One immutable index generation. The three dictionaries serve every consumer (legacy search
///     modes, RPC, catalog views); <see cref="Relevance" /> adds the lookup structures ranked
///     retrieval needs so it never traverses the name set.
/// </summary>
/// <remarks>
///     <see cref="Relevance" /> is derived from <see cref="ByNames" /> by
///     <see cref="PackageIndexBuilder" />, so a hand-built instance (a <c>with</c> expression that
///     replaces only <see cref="ByNames" />) carries an empty relevance index and ranks nothing.
///     Build through the builder whenever ranked retrieval is under test.
/// </remarks>
public sealed record SearchIndexData(
    ImmutableDictionary<string, AurPackageMetadata> ByNames,
    ImmutableDictionary<string, ImmutableHashSet<string>> ByProvides,
    ImmutableDictionary<string, ImmutableHashSet<string>> ByWords,
    RelevanceIndex Relevance)
{
    public static SearchIndexData Empty { get; } = new(
        ImmutableDictionary<string, AurPackageMetadata>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.WithComparers(StringComparer.Ordinal),
        RelevanceIndex.Empty
    );
}
