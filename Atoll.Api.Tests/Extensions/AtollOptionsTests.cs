using Atoll.Api.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Extensions;

public class AtollOptionsTests
{
    private static AtollOptions Resolve(IDictionary<string, string?> configuration)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(configuration).Build();

        services.AddAtollOptions(config);
        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<AtollOptions>>().Value;
    }

    [Fact]
    public void Unconfigured_options_fall_back_to_valid_defaults()
    {
        var options = Resolve(new Dictionary<string, string?>());

        Assert.Multiple(() =>
        {
            Assert.Equal(30, options.Caching.RankTtlSeconds);
            Assert.Equal(4, options.Seed.Bulk.Parallelism);
            Assert.Equal("packages", options.Mongo.Collections.Packages);
        });
    }

    [Theory]
    [InlineData("Atoll:DataSource:DataFileUrl", "not-a-url")]
    [InlineData("Atoll:DataSource:RefreshIntervalMinutes", "0")]
    [InlineData("Atoll:Mongo:ConnectionString", "")]
    [InlineData("Atoll:Mongo:MaxRevisions", "999")]
    [InlineData("Atoll:Mongo:Collections:AurMetadata", "")]
    [InlineData("Atoll:Seed:Direct:SeedDelayMs", "99")]
    [InlineData("Atoll:Seed:Bulk:MirrorUrl", "not-a-url")]
    [InlineData("Atoll:Seed:Bulk:BatchSize", "0")]
    [InlineData("Atoll:Refresh:MaxStalenessHours", "0")]
    [InlineData("Atoll:Security:ScannerConcurrency", "0")]
    [InlineData("Atoll:Ui:ExternalBaseUrl", "not-a-url")]
    [InlineData("Atoll:Caching:DashboardTtlSeconds", "0")]
    public void Nested_annotations_are_enforced(string key, string value)
    {
        Assert.Throws<OptionsValidationException>(() => Resolve(new Dictionary<string, string?> { [key] = value }));
    }

    [Theory]
    [InlineData("Atoll:Caching:RankTtlSeconds", "0", "AtollOptions.Caching", "RankTtlSeconds")]
    [InlineData("Atoll:Mongo:Collections:SeedExclusions", "", "AtollOptions.Mongo.Collections", "SeedExclusions")]
    [InlineData("Atoll:Seed:Bulk:Parallelism", "0", "AtollOptions.Seed.Bulk", "Parallelism")]
    public void Nested_failure_names_the_qualifying_path(string key, string value, string sectionPath, string memberName)
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?> { [key] = value }));

        Assert.Multiple(() =>
        {
            Assert.Contains(sectionPath, exception.Message, StringComparison.Ordinal);
            Assert.Contains(memberName, exception.Message, StringComparison.Ordinal);
        });
    }
}
