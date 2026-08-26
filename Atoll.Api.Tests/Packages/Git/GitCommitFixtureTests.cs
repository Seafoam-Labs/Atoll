using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages.Git;

/// <summary>
///     Pins the exact Git objects Atoll synthesizes for a fixed revision history. Refactorings of
///     the materialization pipeline must keep these byte-for-byte stable: served SHAs are the
///     mirror's public namespace and cannot silently change. Inputs are fully deterministic
///     (fixed dates, authors, messages, and file content), so every SHA below is reproducible.
/// </summary>
[Category("RequiresGit")]
public class GitCommitFixtureTests
{
    private const string Commit1 = "09bc074ee0a6d9c56449bce97bef3941797fdc54";
    private const string Commit2 = "e34691f37ebcf30e13fa6eb5488ed4c0c0cd0f55";

    // Exercises the SanitizeIdent input path; see the author-identity note in the assertion.
    private const string WeirdAuthor = "weird <au>thor\nx";
    private static readonly DateTimeOffset T1 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyDictionary<string, string> Rev1Files =>
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=fixture\npkgver=1.0\n",
            ["README.md"] = "# fixture\n",
            ["install.sh"] = "make install\n",
            ["notes.txt"] = "#!/usr/bin/env cat\njust text\n"
        };

    private static IReadOnlyDictionary<string, string> Rev2Files =>
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=fixture\npkgver=2.0\n",
            ["README.md"] = "# fixture v2\n",
            ["install.sh"] = "make install\n",
            ["notes.txt"] = "#!/usr/bin/env cat\njust text\n"
        };

    private static async Task<bool> GitIsAvailable()
    {
        var (exitCode, _) = await GitClient.TryExecuteAsync(["--version"], CancellationToken.None);
        return exitCode == 0;
    }

    [SetUp]
    public async Task SetUp()
    {
        Assume.That(await GitIsAvailable(), "git binary is required for these tests");
    }

    [Test]
    public async Task Synthesized_history_is_pinned()
    {
        var reposRoot = Path.Combine(Path.GetTempPath(), $"atoll-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reposRoot);
        try
        {
            var repo = new InMemoryPackageRepository();
            var security = new InMemoryPackageSecurityRepository();
            var options = Options.Create(new AtollOptions
            {
                Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 },
                Git = new GitOptions { RepositoriesPath = reposRoot }
            });
            var service = new MongoPackageService(
                repo,
                new PackageIndexStore(),
                options,
                security,
                NullLogger<MongoPackageService>.Instance);

            await InsertVerifiedHistoryAsync(repo, security);
            await service.EnsureGitRepositoryAsync("fixture");

            var gitDir = service.GetRepositoryPath("fixture")!;
            var ct = CancellationToken.None;
            var mainRef = (await GitClient.ExecuteAsync(gitDir, ["rev-parse", "refs/heads/main"], null, null, ct)).Trim();
            var commits = (await GitClient.ExecuteAsync(gitDir, ["rev-list", "main"], null, null, ct))
                .Trim().Split('\n');
            var logLines = (await GitClient.ExecuteAsync(
                    gitDir, ["log", "--format=%H|%an|%ae|%at|%cn|%ce|%ct|%s", "main"], null, null, ct))
                .Trim().Split('\n');
            var treeLines = (await GitClient.ExecuteAsync(gitDir, ["ls-tree", "main"], null, null, ct))
                .Trim().Split('\n');

            Assert.Multiple(() =>
            {
                Assert.That(commits, Has.Length.EqualTo(2));
                Assert.That(mainRef, Is.EqualTo(Commit2));

                // Newest first: the head revision's commit, then its parent.
                Assert.That(commits[0], Is.EqualTo(Commit2));
                Assert.That(commits[1], Is.EqualTo(Commit1));

                // Author identity quirk pinned as-is: SanitizeIdent calls ToString() on the
                // lazy IEnumerable<char>, so every commit's author name is the runtime's
                // iterator type name (and the email appends @atoll.local). The revision's own
                // author string never reaches git. Changing this is a deliberate behavior
                // change to SanitizeIdent, not a refactor.
                Assert.That(logLines[0],
                    Is.EqualTo(Commit2 + "|System.Linq.Enumerable+IEnumerableWhereIterator`1[System.Char]"
                                       + "|System.Linq.Enumerable+IEnumerableWhereIterator`1[System.Char]@atoll.local"
                                       + "|1767355200|atoll|atoll@local|1767355200|refresh from AUR"));
                Assert.That(logLines[1],
                    Is.EqualTo(Commit1 + "|System.Linq.Enumerable+IEnumerableWhereIterator`1[System.Char]"
                                       + "|System.Linq.Enumerable+IEnumerableWhereIterator`1[System.Char]@atoll.local"
                                       + "|1767268800|atoll|atoll@local|1767268800|seed from AUR"));

                // install.sh is executable by extension; notes.txt by its #! content; the rest 100644.
                Assert.That(treeLines[0], Is.EqualTo("100644 blob 9bb14277bc9d9e1c6bed4fe509eecace187af584\tPKGBUILD"));
                Assert.That(treeLines[1], Is.EqualTo("100644 blob 3c7fb5362a50d005e270ecb67a36a05809ba19f2\tREADME.md"));
                Assert.That(treeLines[2], Is.EqualTo("100755 blob e6fe4f7638418d346bbad69e6226fc026b4ef592\tinstall.sh"));
                Assert.That(treeLines[3], Is.EqualTo("100755 blob 0a7e17b41502f961195e1a29eee9150baaf209d2\tnotes.txt"));
            });
        }
        finally
        {
            TryCleanup(reposRoot);
        }
    }

    private static async Task InsertVerifiedHistoryAsync(
        InMemoryPackageRepository repo,
        InMemoryPackageSecurityRepository security)
    {
        await repo.InsertSeedAsync(
            new PackageDocument
            {
                Id = "fixture",
                PackageName = "fixture",
                CreatedAt = T1,
                UpdatedAt = T1,
                HeadRevisionId = "rev-1",
                Revisions =
                [
                    new PackageRevisionDocument
                    {
                        RevisionId = "rev-1",
                        CreatedAt = T1,
                        Author = WeirdAuthor,
                        Message = "seed from AUR"
                    }
                ]
            },
            Content("rev-1", T1, WeirdAuthor, "seed from AUR", Rev1Files));

        await repo.AppendRevisionAsync(
            "fixture",
            Content("rev-2", T2, "aur", "refresh from AUR", Rev2Files),
            10);

        await VerifyAsync(security, "rev-1");
        await VerifyAsync(security, "rev-2");
    }

    private static async Task VerifyAsync(InMemoryPackageSecurityRepository security, string revisionId)
    {
        await security.MarkPendingAsync("fixture", revisionId, true);
        // Claims the just-marked pending scan and completes it Verified, like the scan worker.
        await security.CompleteScanAsync("fixture", SecurityStatus.Verified);
    }

    private static PackageRevisionContentDocument Content(
        string revisionId,
        DateTimeOffset createdAt,
        string author,
        string message,
        IReadOnlyDictionary<string, string> files)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("fixture", revisionId),
            PackageName = "fixture",
            RevisionId = revisionId,
            CreatedAt = createdAt,
            Author = author,
            Message = message,
            Files = files.ToDictionary(
                kv => kv.Key,
                kv => new PackageFile { Content = kv.Value, Size = 0, Hash = "unused" })
        };
    }

    private static void TryCleanup(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // ignore
        }
    }
}