using System.Security.Cryptography;
using System.Text;
using Atoll.Api.Services.Packages;
using Xunit;

namespace Atoll.Api.Tests.Packages;

public class PackageSnapshotFactoryTests
{
    [Fact]
    public void Create_Utf8Content_MeasuresSizeAndHashOverEncodedBytes()
    {
        var content = "héllo → 🌍";
        var name = "ünïcode.txt";

        var snapshot = PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, string>(StringComparer.Ordinal) { [name] = content }, 5_242_880, "aur", "seed from AUR");

        var bytes = Encoding.UTF8.GetBytes(content);
        var file = snapshot.Content.Files[name];
        var expectedHash = $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

        Assert.Multiple(() =>
        {
            Assert.Equal(bytes.Length, file.Size);
            Assert.NotEqual(content.Length, file.Size);
            Assert.Equal(content, file.Content);
            Assert.Equal(expectedHash, file.Hash);
        });
    }

    [Fact]
    public void Create_ReorderedFiles_ProducesDeterministicRevisionId()
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal) { ["a.txt"] = "one", ["b.txt"] = "two", ["c.txt"] = "three" };
        var reordered = new Dictionary<string, string>(StringComparer.Ordinal) { ["c.txt"] = "three", ["a.txt"] = "one", ["b.txt"] = "two" };
        var changed = new Dictionary<string, string>(StringComparer.Ordinal) { ["a.txt"] = "one", ["b.txt"] = "two", ["c.txt"] = "changed" };

        var snapshot1 = PackageSnapshotFactory.Create("pkg", first, 5_242_880, "aur", "seed from AUR");
        var snapshot2 = PackageSnapshotFactory.Create("pkg", reordered, 5_242_880, "aur", "seed from AUR");
        var snapshot3 = PackageSnapshotFactory.Create("pkg", changed, 5_242_880, "aur", "seed from AUR");

        Assert.Multiple(() =>
        {
            Assert.Equal(snapshot1.RevisionId, snapshot2.RevisionId);
            Assert.NotEqual(snapshot1.RevisionId, snapshot3.RevisionId, StringComparer.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", snapshot1.RevisionId);
        });
    }

    [Fact]
    public void Create_Snapshot_PopulatesContentAndMetadataDocuments()
    {
        var snapshot = PackageSnapshotFactory.Create(
            "shelly", new Dictionary<string, string>(StringComparer.Ordinal) { ["PKGBUILD"] = "pkgname=shelly\n" }, 5_242_880, "aur", "seed from AUR");

        Assert.Multiple(() =>
        {
            Assert.Equal($"shelly:{snapshot.RevisionId}", snapshot.Content.Id);
            Assert.Equal("shelly", snapshot.Content.PackageName);
            Assert.Equal(snapshot.RevisionId, snapshot.Content.RevisionId);
            Assert.Equal("aur", snapshot.Content.Author);
            Assert.Equal("seed from AUR", snapshot.Content.Message);
            Assert.Equal(snapshot.CreatedAt, snapshot.Content.CreatedAt);
            Assert.Equivalent(new[] { "PKGBUILD" }, snapshot.Content.Files.Keys, strict: true);
            Assert.Equal(snapshot.RevisionId, snapshot.Metadata.RevisionId);
            Assert.Equal(snapshot.CreatedAt, snapshot.Metadata.CreatedAt);
            Assert.Equal("aur", snapshot.Metadata.Author);
            Assert.Equal("seed from AUR", snapshot.Metadata.Message);
            Assert.Equal(DateTimeOffset.UtcNow, snapshot.CreatedAt, TimeSpan.FromMinutes(5));
        });
    }

    [Fact]
    public void Create_FileExactlyAtPerFileLimit_Accepts()
    {
        var snapshot = PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, string>(StringComparer.Ordinal) { ["a.txt"] = new string('a', 10) }, 10, "aur", "seed from AUR");

        Assert.Equal(10, snapshot.Content.Files["a.txt"].Size);
    }

    [Fact]
    public void Create_FileOverPerFileLimit_Rejects()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, string>(StringComparer.Ordinal) { ["big.txt"] = new string('a', 11) }, 10, "aur", "seed from AUR"));

        Assert.Equal(
            "File 'big.txt' is 11 bytes which exceeds the per-file limit of 10 bytes.",
            ex!.Message);
    }

    [Fact]
    public void Create_MultiByteContentOverPerFileLimit_RejectsOnUtf8Bytes()
    {
        var sixCharacters = "🌍🌍";

        var ex = Assert.Throws<InvalidOperationException>(() => PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, string>(StringComparer.Ordinal) { ["a.txt"] = sixCharacters }, 7, "aur", "seed from AUR"));

        Assert.Equal(
            "File 'a.txt' is 8 bytes which exceeds the per-file limit of 7 bytes.",
            ex!.Message);
    }
}
