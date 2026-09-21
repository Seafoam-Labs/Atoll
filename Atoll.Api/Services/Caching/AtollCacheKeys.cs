using Atoll.Api.Services.Packages;

namespace Atoll.Api.Services.Caching;

/// <summary>
///     Key and tag vocabulary for the shared HybridCache. Keys are derived enums/consts only, never
///     user input.
/// </summary>
internal static class AtollCacheKeys
{
    public const string TagCatalog = "catalog";

    public const string TagHeadStatus = "head-status";

    public const string RankNames = "atoll.rank.names";

    public const string SeededSnapshot = "atoll.ui.seeded-snapshot";

    public const string StatusDashboard = "atoll.ui.status-dashboard";

    public static string RankSorted(PackageIndexSortBy sortBy, PackageIndexSortOrder order) =>
        $"atoll.rank.sorted/{sortBy}/{order}";
}
