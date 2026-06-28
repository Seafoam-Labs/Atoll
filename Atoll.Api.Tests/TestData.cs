using Atoll.Api.Services.Search.Indexing;

namespace Atoll.Api.Tests;

internal static class TestData
{
    private const string SamplePackagesJson =
        """
        [
          {
            "Name": "shelly-bin",
            "Description": "Shelly: A Modern Arch Package Manager (prebuilt binary)",
            "Provides": ["shelly"],
            "Keywords": ["helper", "AUR"],
            "NumVotes": 10
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
        return await PackageDataLoader.LoadAsync(path, CancellationToken.None);
    }
}