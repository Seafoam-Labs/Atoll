using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Packages;

public class PackageRevisionCompactionWorkerTests
{
    private static PackageRevisionCompactionWorker CreateWorker(InMemoryPackageRepository repo, int maxRevisions)
    {
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxRevisions = maxRevisions }
        });
        return new PackageRevisionCompactionWorker(
            repo, options, NullLogger<PackageRevisionCompactionWorker>.Instance);
    }

    /// <summary>Seeds rev-1 and appends rev-2 up to rev-<paramref name="revisionCount"/> without trimming.</summary>
    private static async Task SeedWithHistoryAsync(InMemoryPackageRepository repo, int revisionCount)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        await repo.InsertSeedAsync(
            new PackageDocument
            {
                Id = "shelly",
                PackageName = "shelly",
                CreatedAt = now,
                UpdatedAt = now,
                HeadRevisionId = "rev-1",
                Revisions =
                [
                    new PackageRevisionDocument
                    {
                        RevisionId = "rev-1",
                        CreatedAt = now,
                        Author = "test",
                        Message = "seed"
                    }
                ]
            },
            RevisionContent("rev-1"), ct);

        for (var i = 2; i <= revisionCount; i++)
            await repo.AppendRevisionAsync("shelly", RevisionContent($"rev-{i}"), maxRevisions: 100, ct);
    }

    private static PackageRevisionContentDocument RevisionContent(string revisionId)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("shelly", revisionId),
            PackageName = "shelly",
            RevisionId = revisionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = revisionId,
            Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
        };
    }

    [Fact]
    public async Task CompactAsync_trims_history_and_deletes_evicted_revisions()
    {
        var repo = new InMemoryPackageRepository();
        await SeedWithHistoryAsync(repo, revisionCount: 7);
        var worker = CreateWorker(repo, maxRevisions: 5);

        var trimmed = await worker.CompactAsync(TestContext.Current.CancellationToken);

        var head = await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken);
        var evicted = await repo.GetRevisionAsync("shelly", "rev-2", TestContext.Current.CancellationToken);
        var retained = await repo.GetRevisionAsync("shelly", "rev-3", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, trimmed);
            Assert.NotNull(head);
            Assert.Equal(
                ["rev-7", "rev-6", "rev-5", "rev-4", "rev-3"],
                head!.Revisions.Select(r => r.RevisionId), StringComparer.Ordinal);
            Assert.Equal("rev-7", head.HeadRevisionId);
            Assert.Null(evicted);
            Assert.NotNull(retained);
        });
    }

    [Fact]
    public async Task CompactAsync_leaves_packages_at_the_cap_untouched()
    {
        var repo = new InMemoryPackageRepository();
        await SeedWithHistoryAsync(repo, revisionCount: 5);
        var worker = CreateWorker(repo, maxRevisions: 5);

        var trimmed = await worker.CompactAsync(TestContext.Current.CancellationToken);

        var head = await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken);
        var oldest = await repo.GetRevisionAsync("shelly", "rev-1", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(0, trimmed);
            Assert.Equal(5, head!.Revisions.Count);
            Assert.NotNull(oldest);
        });
    }

    [Fact]
    public async Task Startup_runs_the_compaction_pass()
    {
        var repo = new InMemoryPackageRepository();
        await SeedWithHistoryAsync(repo, revisionCount: 7);
        var worker = CreateWorker(repo, maxRevisions: 5);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        var head = await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken);
        Assert.Equal(5, head!.Revisions.Count);
    }
}