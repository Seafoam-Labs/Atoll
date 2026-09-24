namespace Atoll.Api.Services.Catalog.Refresh;

/// <summary>
///     What one refresh cycle did. Only <see cref="Refreshed" /> swaps in a new index generation, and
///     only <see cref="Failed" /> reached no archive at all, which is the one outcome the worker does
///     not warm the derived views after.
/// </summary>
public enum PackageIndexRefreshOutcome
{
    NotModified,
    Refreshed,
    Failed
}