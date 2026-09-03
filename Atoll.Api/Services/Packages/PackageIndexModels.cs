using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Packages;

public sealed record PackageIndexResponse(
    IReadOnlyList<PackageIndexEntry> Items,
    int Page,
    int Limit,
    long TotalItems,
    int TotalPages);
