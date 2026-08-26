using Atoll.Api.Services.Catalog.Indexing;

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
        await File.WriteAllTextAsync(path, SamplePackagesJson);
        return path;
    }

    internal static async Task<SearchIndexData> LoadSampleIndexesAsync()
    {
        var path = await WriteSamplePackagesAsync();
        return await PackageIndexBuilder.LoadAsync(path, CancellationToken.None);
    }
}