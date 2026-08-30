using System.Security.Cryptography;
using System.Text;
using Atoll.Api.Services.Packages;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages;

public class PackageSnapshotFactoryTests
{
    [Test]
    public void Create_measures_size_and_hash_over_utf8_bytes()
    {
        var content = "héllo → 🌍";
        var name = "ünïcode.txt";

        var snapshot = PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, string> { [name] = content }, 5_242_880, "aur", "seed from AUR");

        var bytes = Encoding.UTF8.GetBytes(content);
        var file = snapshot.Content.Files[name];
        var expectedHash = $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

        Assert.Multiple(() =>
        {
            Assert.That(file.Size, Is.EqualTo(bytes.Length));
            Assert.That(file.Size, Is.Not.EqualTo(content.Length));
            Assert.That(file.Content, Is.EqualTo(content));
            Assert.That(file.Hash, Is.EqualTo(expectedHash));
        });
    }

    [Test]
    public void Create_revision_id_is_deterministic_and_order_independent()
    {
        var first = new Dictionary<string, string> { ["a.txt"] = "one", ["b.txt"] = "two", ["c.txt"] = "three" };
        var reordered = new Dictionary<string, string> { ["c.txt"] = "three", ["a.txt"] = "one", ["b.txt"] = "two" };
        var changed = new Dictionary<string, string> { ["a.txt"] = "one", ["b.txt"] = "two", ["c.txt"] = "changed" };

        var snapshot1 = PackageSnapshotFactory.Create("pkg", first, 5_242_880, "aur", "seed from AUR");
        var snapshot2 = PackageSnapshotFactory.Create("pkg", reordered, 5_242_880, "aur", "seed from AUR");
        var snapshot3 = PackageSnapshotFactory.Create("pkg", changed, 5_242_880, "aur", "seed from AUR");

        Assert.Multiple(() =>
        {
            Assert.That(snapshot2.RevisionId, Is.EqualTo(snapshot1.RevisionId));
            Assert.That(snapshot3.RevisionId, Is.Not.EqualTo(snapshot1.RevisionId));
            Assert.That(snapshot1.RevisionId, Does.Match("^[0-9a-f]{64}$"));
        });
    }

    [Test]
    public void Create_populates_content_and_metadata_documents()
    {
        var snapshot = PackageSnapshotFactory.Create(
            "shelly", new Dictionary<string, string> { ["PKGBUILD"] = "pkgname=shelly\n" }, 5_242_880, "aur", "seed from AUR");

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Content.Id, Is.EqualTo($"shelly:{snapshot.RevisionId}"));
            Assert.That(snapshot.Content.PackageName, Is.EqualTo("shelly"));
            Assert.That(snapshot.Content.RevisionId, Is.EqualTo(snapshot.RevisionId));
            Assert.That(snapshot.Content.Author, Is.EqualTo("aur"));
            Assert.That(snapshot.Content.Message, Is.EqualTo("seed from AUR"));
            Assert.That(snapshot.Content.CreatedAt, Is.EqualTo(snapshot.CreatedAt));
            Assert.That(snapshot.Content.Files.Keys, Is.EquivalentTo(["PKGBUILD"]));
            Assert.That(snapshot.Metadata.RevisionId, Is.EqualTo(snapshot.RevisionId));
            Assert.That(snapshot.Metadata.CreatedAt, Is.EqualTo(snapshot.CreatedAt));
            Assert.That(snapshot.Metadata.Author, Is.EqualTo("aur"));
            Assert.That(snapshot.Metadata.Message, Is.EqualTo("seed from AUR"));
            Assert.That(snapshot.CreatedAt, Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(5)));
        });
    }

    [Test]
    public void Create_accepts_file_exactly_at_per_file_limit()
    {
        var snapshot = PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, string> { ["a.txt"] = new string('a', 10) }, 10, "aur", "seed from AUR");

        Assert.That(snapshot.Content.Files["a.txt"].Size, Is.EqualTo(10));
    }

    [Test]
    public void Create_rejects_file_exceeding_per_file_limit()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, string> { ["big.txt"] = new string('a', 11) }, 10, "aur", "seed from AUR"));

        Assert.That(ex!.Message,
            Is.EqualTo("File 'big.txt' is 11 bytes which exceeds the per-file limit of 10 bytes."));
    }

    [Test]
    public void Create_enforces_per_file_limit_on_utf8_bytes_not_characters()
    {
        var sixCharacters = "🌍🌍";

        var ex = Assert.Throws<InvalidOperationException>(() => PackageSnapshotFactory.Create(
            "pkg", new Dictionary<string, string> { ["a.txt"] = sixCharacters }, 7, "aur", "seed from AUR"));

        Assert.That(ex!.Message,
            Is.EqualTo("File 'a.txt' is 8 bytes which exceeds the per-file limit of 7 bytes."));
    }
}
