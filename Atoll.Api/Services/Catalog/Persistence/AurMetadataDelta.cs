namespace Atoll.Api.Services.Catalog.Persistence;

/// <summary>
///     The difference between the persisted metadata copy and a freshly parsed dump: the documents
///     whose content actually moved, plus the names that vanished upstream.
/// </summary>
public sealed record AurMetadataDelta(
    IReadOnlyList<AurPackageMetadata> Upserts,
    IReadOnlyList<string> Removals,
    int Unchanged)
{
    public bool IsEmpty => Upserts.Count == 0 && Removals.Count == 0;

    /// <summary>
    ///     Diffs a parsed snapshot against the persisted one. Snapshot entries are keyed by name with
    ///     the last entry winning, matching the index builder's rule for a repeated name; two upserts
    ///     for one id in an unordered bulk write would otherwise have no defined winner.
    /// </summary>
    public static AurMetadataDelta Compute(
        IReadOnlyDictionary<string, AurPackageMetadata> previous,
        IEnumerable<AurPackageMetadata> snapshot)
    {
        var current = new Dictionary<string, AurPackageMetadata>(StringComparer.Ordinal);
        foreach (var package in snapshot) current[package.Name] = package;

        List<AurPackageMetadata>? upserts = null;
        var unchanged = 0;

        foreach (var (name, package) in current)
        {
            if (previous.TryGetValue(name, out var persisted) && persisted.Equals(package))
            {
                unchanged++;
                continue;
            }

            (upserts ??= []).Add(package);
        }

        List<string>? removals = null;
        foreach (var name in previous.Keys)
        {
            if (!current.ContainsKey(name)) (removals ??= []).Add(name);
        }

        return new AurMetadataDelta(upserts ?? [], removals ?? [], unchanged);
    }
}
