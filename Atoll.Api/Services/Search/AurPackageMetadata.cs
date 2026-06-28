namespace Atoll.Api.Services.Search;

/// <summary>
///     Represents a package from the AUR package repository.
/// </summary>
public sealed record AurPackageMetadata(
    long Id,
    string Name,
    long PackageBaseId,
    string PackageBase,
    string Version,
    string Description,
    string? Url,
    long NumVotes,
    double Popularity,
    long? OutOfDate,
    string? Maintainer,
    string? Submitter,
    long FirstSubmitted,
    long LastModified,
    string UrlPath,
    IReadOnlyList<string> Depends,
    IReadOnlyList<string> MakeDepends,
    IReadOnlyList<string> OptDepends,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> License,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> CoMaintainers
);