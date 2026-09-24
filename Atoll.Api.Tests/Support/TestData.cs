using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Xunit;

namespace Atoll.Api.Tests.Support;

internal static class TestData
{
    private const string SamplePackagesJson =
        """
        [
          {
            "ID": 101,
            "Name": "shelly-bin",
            "PackageBaseID": 100,
            "PackageBase": "shelly",
            "Version": "1.2.3-1",
            "Description": "Shelly: A Modern Arch Package Manager (prebuilt binary)",
            "URL": "https://example.test/shelly",
            "Provides": ["shelly"],
            "Depends": ["pacman>=6"],
            "CheckDepends": ["bats"],
            "Groups": ["atoll-test"],
            "Replaces": ["shelly-old"],
            "License": ["MIT"],
            "Keywords": ["helper", "AUR"],
            "NumVotes": 10,
            "OutOfDate": 1735689600,
            "Maintainer": "alice"
          },
          {
            "Name": "portable-kit",
            "Description": "Handheld gaming toolkit 1337 i3",
            "Keywords": ["handheld"],
            "NumVotes": 5
          },
          {
            "Name": "portable-pro",
            "Description": "Handheld gaming emulator",
            "Provides": ["portable"],
            "Keywords": ["emulator", "fast"],
            "NumVotes": 20
          }
        ]
        """;

    internal static async Task<string> WriteSamplePackagesAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"atoll-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, SamplePackagesJson, TestContext.Current.CancellationToken);
        return path;
    }

    internal static SearchIndexData LoadSampleIndexes() => PackageIndexBuilder.Parse(SamplePackagesJson);

    /// <summary>A catalog entry with every non-essential field empty, for synthetic corpora.</summary>
    internal static AurPackageMetadata Package(string name, long votes = 0, string[]? provides = null)
    {
        return new AurPackageMetadata(
            Id: 0,
            Name: name,
            PackageBaseId: 0,
            PackageBase: name,
            Version: "1.0-1",
            Description: "",
            Url: null,
            NumVotes: votes,
            Popularity: 0,
            OutOfDate: null,
            Maintainer: null,
            Submitter: null,
            FirstSubmitted: 0,
            LastModified: 0,
            UrlPath: "",
            Depends: [],
            MakeDepends: [],
            OptDepends: [],
            Conflicts: [],
            Provides: provides ?? [],
            License: [],
            Keywords: [],
            CoMaintainers: []);
    }

    /// <summary>Indexes bare names, for tests that only need a corpus of a given size or key set.</summary>
    internal static SearchIndexData IndexFromNames(IEnumerable<string> names)
        => PackageIndexBuilder.BuildFromPackages(names.Select(name => Package(name)));
}
