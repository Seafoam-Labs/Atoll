using System.Collections.Immutable;
using Atoll.Api.Services.Caching;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Packages;

public sealed class PackageService(
    IPackageRepository repo,
    IOptions<AtollOptions> options,
    IPackageSecurityRepository securityRepository,
    IPackageSecurityScanner scanner,
    IGitRepositoryCache gitCache,
    HybridCache hybridCache,
    PackageIndexStore indexStore) : IPackageService
{
    public const int DefaultIndexPageLimit = 50;
    public const int MaxIndexPageLimit = 200;

    private readonly AtollOptions _options = options.Value;

    // Internal implementation detail built from the same dependencies rather than registered:
    // PackageIndexRanker has no other consumers.
    private readonly PackageIndexRanker _ranker = new(repo, hybridCache, indexStore);

    public Task<IReadOnlyList<string>> ListAsync()
    {
        return repo.ListAsync();
    }

    public async Task<int> CountAsync()
    {
        return (int)await repo.CountAsync();
    }

    public async Task<PackageIndexResponse> GetIndexPageAsync(
        int page,
        int limit,
        PackageIndexSortBy sortBy = PackageIndexSortBy.Name,
        PackageIndexSortOrder? order = null,
        CancellationToken ct = default)
    {
        var direction = order ?? PackageIndexSortOrder.Asc;
        var catalog = indexStore.Current.ByNames;

        // Name ascending pages directly in MongoDB. Everything else ranks the seeded set through
        // PackageIndexRanker, which caches one sorted name array per sort key, because votes,
        // popularity, and version live only in the in-memory catalog, not Mongo.
        if (sortBy is PackageIndexSortBy.Name && direction is PackageIndexSortOrder.Asc)
        {
            var total = await repo.CountAsync(ct);
            var totalPages = total == 0 ? 0 : (int)((total + limit - 1) / limit);

            // Guard against arithmetic overflow (e.g. page = int.MaxValue) and skip beyond total/int.MaxValue.
            var skip = (long)(page - 1) * limit;
            if (skip >= total || skip > int.MaxValue)
                return new PackageIndexResponse([], page, limit, total, totalPages);

            var items = await repo.ListIndexPageAsync((int)skip, limit, ct);
            return new PackageIndexResponse(EnrichWithCatalog(items, catalog), page, limit, total, totalPages);
        }

        var sorted = await _ranker.GetSortedNamesAsync(sortBy, direction, ct);
        var rankedTotal = sorted.Length;
        var rankedPages = rankedTotal == 0 ? 0 : (int)(((long)rankedTotal + limit - 1) / limit);

        // Same overflow guard; totals describe the ranked snapshot the page is sliced from.
        var rankedSkip = (long)(page - 1) * limit;
        if (rankedSkip >= rankedTotal || rankedSkip > int.MaxValue)
            return new PackageIndexResponse([], page, limit, rankedTotal, rankedPages);

        var start = (int)rankedSkip;
        var count = Math.Min(limit, rankedTotal - start);
        var pageNames = new string[count];
        Array.Copy(sorted, start, pageNames, 0, count);

        var entries = await repo.ListIndexEntriesAsync(pageNames, ct);

        // The repository returns rows unordered; reorder to the ranked page order. A name deleted
        // between the ranking build and this fetch drops out, shortening the page.
        var byName = entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var ordered = new List<PackageIndexEntry>(count);
        foreach (var name in pageNames)
            if (byName.TryGetValue(name, out var entry))
                ordered.Add(entry);

        return new PackageIndexResponse(EnrichWithCatalog(ordered, catalog), page, limit, rankedTotal, rankedPages);
    }

    private static PackageIndexEntry[] EnrichWithCatalog(
        IReadOnlyList<PackageIndexEntry> items,
        ImmutableDictionary<string, AurPackageMetadata> catalog)
    {
        return items
            .Select(item => catalog.GetValueOrDefault(item.Name) is { } metadata
                ? item with
                {
                    Description = metadata.Description,
                    Version = metadata.Version,
                    NumVotes = metadata.NumVotes,
                    Popularity = metadata.Popularity,
                    OutOfDate = metadata.OutOfDate
                }
                : item)
            .ToArray();
    }

    public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
    {
        return repo.ExistsAsync(packageName, ct);
    }

    public async Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
    {
        string revisionId;
        if (string.IsNullOrEmpty(commitSha))
        {
            var doc = await repo.GetHeadAsync(packageName) ?? throw new KeyNotFoundException($"Package '{packageName}' not found.");
            revisionId = doc.HeadRevisionId;
        }
        else
        {
            revisionId = commitSha;
        }

        var revision = await repo.GetRevisionAsync(packageName, revisionId) ??
                       throw new KeyNotFoundException($"Revision '{revisionId}' not found for package '{packageName}'.");

        return ToPackageFiles(revision.Files);
    }

    public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
    {
        return repo.GetHistoryAsync(packageName);
    }

    public async Task DeleteAsync(string packageName, CancellationToken ct = default)
    {
        // Git owns its derived-state cleanup and materialization lock; Packages owns the
        // authoritative delete. The scope keeps both steps atomic with respect to materialization.
        await using var deletion = await gitCache.BeginDeleteAsync(packageName, ct);
        await repo.DeleteAsync(packageName, ct);
        await hybridCache.RemoveByTagAsync(AtollCacheKeys.TagCatalog, ct);
    }

    public async Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
    {
        var snapshot = PackageSnapshotFactory.Create(
            packageName, files, _options.Mongo.MaxFileBytes, "aur", "seed from AUR");
        PackageDocumentSizeValidator.Validate(packageName, snapshot.Content);

        var doc = new PackageDocument
        {
            Id = packageName,
            PackageName = packageName,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.CreatedAt,
            HeadRevisionId = snapshot.RevisionId,
            Revisions = [snapshot.Metadata]
        };

        await repo.InsertSeedAsync(doc, snapshot.Content);
        await securityRepository.MarkPendingAsync(packageName, snapshot.RevisionId, true, scanner.PolicyVersion);
        await hybridCache.RemoveByTagAsync(AtollCacheKeys.TagCatalog);
    }

    public async Task<bool> AppendRevisionFromUpstreamAsync(
        string packageName,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct = default)
    {
        var snapshot = PackageSnapshotFactory.Create(
            packageName, files, _options.Mongo.MaxFileBytes, "aur", "refresh from AUR");

        var current = await repo.GetHeadAsync(packageName, ct);
        if (current is null)
            throw new KeyNotFoundException($"Package '{packageName}' not found.");

        if (snapshot.RevisionId == current.HeadRevisionId)
            return false;

        PackageDocumentSizeValidator.Validate(packageName, snapshot.Content);

        await repo.AppendRevisionAsync(packageName, snapshot.Content, _options.Mongo.MaxRevisions, ct);

        await securityRepository.MarkPendingAsync(packageName, snapshot.RevisionId, true, scanner.PolicyVersion, ct);
        await securityRepository.PromoteHeadAsync(packageName, snapshot.RevisionId, ct);

        return true;
    }

    private static PackageFiles ToPackageFiles(IReadOnlyDictionary<string, PackageFile> files)
    {
        return new PackageFiles(files.ToDictionary(kv => kv.Key, kv => kv.Value.Content));
    }
}