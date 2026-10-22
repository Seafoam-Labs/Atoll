using System.Security.Cryptography;
using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Xunit;

namespace Atoll.Api.Tests.Packages;

public class PackageSnapshotFactoryTests
{
    private const int DefaultMaxFileBytes = 5_242_880;

    private static readonly byte[] PngLikePayload =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0xFF, 0xFE, 0x80, 0x41, 0x42
    ];

    [Fact]
    public void Create_Utf8Content_MeasuresSizeAndHashOverEncodedBytes()
    {
        var content = "héllo → 🌍";
        var name = "ünïcode.txt";
        var bytes = Encoding.UTF8.GetBytes(content);

        var snapshot = PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, byte[]>(StringComparer.Ordinal) { [name] = bytes },
            DefaultMaxFileBytes, "aur", "seed from AUR");

        var file = snapshot.Content.Files[name];
        var expectedHash = $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

        Assert.Multiple(() =>
        {
            Assert.Equal(bytes.Length, file.Size);
            Assert.NotEqual(content.Length, file.Size);
            Assert.Equal(bytes, file.Content);
            Assert.Equal(expectedHash, file.Hash);
        });
    }

    [Fact]
    public void Create_InvalidUtf8Content_PreservesRawBytesAndHashesThemDirectly()
    {
        var snapshot = PackageSnapshotFactory.Create(
            "burpsuite",
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["icon64.png"] = PngLikePayload },
            DefaultMaxFileBytes, "aur", "seed from AUR");

        var file = snapshot.Content.Files["icon64.png"];
        var expectedHash = $"sha256:{Convert.ToHexString(SHA256.HashData(PngLikePayload)).ToLowerInvariant()}";

        Assert.Multiple(() =>
        {
            Assert.Equal(PngLikePayload.Length, file.Size);
            Assert.Equal(PngLikePayload, file.ContentBytes);
            Assert.Equal(PngLikePayload, file.Content);
            Assert.Equal(expectedHash, file.Hash);
            Assert.Equal(-1, file.Content.AsSpan().IndexOf("\uFFFD"u8));
        });
    }

    [Fact]
    public void Create_TextRevision_ProducesThePinnedPreFixRevisionId()
    {
        // Pins the existing revision-id format: changing it would rewrite every package's
        // history. The value was computed with the pre-fix string-based factory.
        var snapshot = PackageSnapshotFactory.Create(
            "shelly", TestFiles.Text(("PKGBUILD", "pkgname=shelly\n")),
            DefaultMaxFileBytes, "aur", "seed from AUR");

        Assert.Equal("917327d5b73d8f15e7617d797cffc2544077ba870d75b202d1e86f1a6615eabd", snapshot.RevisionId);
    }

    [Fact]
    public void Create_ReorderedFiles_ProducesDeterministicRevisionId()
    {
        var first = TestFiles.Text(("a.txt", "one"), ("b.txt", "two"), ("c.txt", "three"));
        var reordered = TestFiles.Text(("c.txt", "three"), ("a.txt", "one"), ("b.txt", "two"));
        var changed = TestFiles.Text(("a.txt", "one"), ("b.txt", "two"), ("c.txt", "changed"));

        var snapshot1 = PackageSnapshotFactory.Create("pkg", first, DefaultMaxFileBytes, "aur", "seed from AUR");
        var snapshot2 = PackageSnapshotFactory.Create("pkg", reordered, DefaultMaxFileBytes, "aur", "seed from AUR");
        var snapshot3 = PackageSnapshotFactory.Create("pkg", changed, DefaultMaxFileBytes, "aur", "seed from AUR");

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
            "shelly", TestFiles.Text(("PKGBUILD", "pkgname=shelly\n")),
            DefaultMaxFileBytes, "aur", "seed from AUR");

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
    public void Create_SerializedRevision_WritesContentBytesAndNoLegacyContent()
    {
        var snapshot = PackageSnapshotFactory.Create(
            "burpsuite",
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["icon64.png"] = PngLikePayload },
            DefaultMaxFileBytes, "aur", "seed from AUR");

        var bson = snapshot.Content.ToBsonDocument();
        var file = bson["files"].AsBsonDocument["icon64.png"].AsBsonDocument;

        Assert.Multiple(() =>
        {
            Assert.True(file.Contains("contentBytes"));
            Assert.False(file.Contains("content"));
            Assert.Equal(BsonType.Binary, file["contentBytes"].BsonType);
            Assert.Equal(PngLikePayload, file["contentBytes"].AsByteArray);
        });

        var roundTripped = BsonSerializer.Deserialize<PackageRevisionContentDocument>(bson);
        Assert.Equal(PngLikePayload, roundTripped.Files["icon64.png"].Content);
    }

    [Fact]
    public void Deserialize_LegacyContentString_ReEncodesToTheSameBytes()
    {
        var content = "pkgname=shelly\n";
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
        var legacy = new BsonDocument
        {
            ["_id"] = "shelly:legacy",
            ["packageName"] = "shelly",
            ["revisionId"] = "legacy",
            ["schemaVersion"] = 2,
            ["files"] = new BsonDocument
            {
                ["PKGBUILD"] = new BsonDocument
                {
                    ["content"] = content,
                    ["size"] = bytes.Length,
                    ["hash"] = hash
                }
            }
        };

        var document = BsonSerializer.Deserialize<PackageRevisionContentDocument>(legacy);
        var file = document.Files["PKGBUILD"];

        Assert.Multiple(() =>
        {
            Assert.Null(file.ContentBytes);
            Assert.Equal(content, file.ContentText);
            Assert.Equal(bytes, file.Content);
            Assert.Equal(bytes.Length, file.Size);
            Assert.Equal(hash, file.Hash);
        });
    }

    [Fact]
    public void Create_FileExactlyAtPerFileLimit_Accepts()
    {
        var snapshot = PackageSnapshotFactory.Create(
            "pkg", TestFiles.Text(("a.txt", new string('a', 10))), 10, "aur", "seed from AUR");

        Assert.Equal(10, snapshot.Content.Files["a.txt"].Size);
    }

    [Fact]
    public void Create_FileOverPerFileLimit_Rejects()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PackageSnapshotFactory.Create(
            "pkg", TestFiles.Text(("big.txt", new string('a', 11))), 10, "aur", "seed from AUR"));

        Assert.Equal(
            "File 'big.txt' is 11 bytes which exceeds the per-file limit of 10 bytes.",
            ex!.Message);
    }

    [Fact]
    public void Create_BinaryFileOverPerFileLimit_RejectsOnRawByteCount()
    {
        var oversized = Enumerable.Repeat((byte)0xFF, 11).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["icon.png"] = oversized },
            10, "aur", "seed from AUR"));

        Assert.Equal(
            "File 'icon.png' is 11 bytes which exceeds the per-file limit of 10 bytes.",
            ex!.Message);
    }

    [Fact]
    public void Create_MultiByteContentOverPerFileLimit_RejectsOnUtf8Bytes()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PackageSnapshotFactory.Create(
            "pkg", TestFiles.Text(("a.txt", "🌍🌍")), 7, "aur", "seed from AUR"));

        Assert.Equal(
            "File 'a.txt' is 8 bytes which exceeds the per-file limit of 7 bytes.",
            ex!.Message);
    }
}