using Atoll.Api.Services.Packages;
using MongoDB.Bson;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages;

public class PackageDocumentSizeValidatorTests
{
    private const long Limit = PackageDocumentSizeValidator.MongoMaxDocumentSizeBytes;

    [Test]
    public void Validate_accepts_document_with_conservative_estimate_below_limit()
    {
        var revision = Revision(("PKGBUILD", 200, "pkgname=shelly\n"));

        Assert.That(
            () => PackageDocumentSizeValidator.Validate("shelly", revision),
            Throws.Nothing);
    }

    [Test]
    public void Validate_accepts_when_estimate_exceeds_limit_but_exact_bson_fits()
    {
        // Declared sizes push the conservative estimate past 16 MiB while the actual
        // content stays tiny, so the exact measurement must accept the document.
        var revision = Revision(
            ("large-1.txt", 9_000_000, "x"),
            ("large-2.txt", 9_000_000, "y"));

        Assert.That(
            () => PackageDocumentSizeValidator.Validate("shelly", revision),
            Throws.Nothing);
    }

    [Test]
    public void Validate_at_exact_estimate_boundary_skips_exact_measurement()
    {
        // size + 1-byte name + 160 + 1024 == 16 MiB exactly: the estimate check is
        // inclusive, so validation passes without ever serializing the document.
        var sizeAtBoundary = 16 * 1024 * 1024 - 1024 - 160 - 1;
        var revision = Revision(("a", sizeAtBoundary, "x"));

        Assert.That(
            () => PackageDocumentSizeValidator.Validate("shelly", revision),
            Throws.Nothing);
    }

    [Test]
    public void Validate_rejects_document_whose_exact_bson_exceeds_limit()
    {
        var revision = Revision(("huge.txt", 16 * 1024 * 1024, new string('a', 16 * 1024 * 1024)));

        var ex = Assert.Throws<PackageDocumentTooLargeException>(
            () => PackageDocumentSizeValidator.Validate("big", revision))!;

        var exactSize = revision.ToBson().LongLength;
        Assert.Multiple(() =>
        {
            Assert.That(ex.PackageName, Is.EqualTo("big"));
            Assert.That(ex.SerializedSizeBytes, Is.EqualTo(exactSize));
            Assert.That(ex.MaxDocumentSizeBytes, Is.EqualTo(Limit));
            Assert.That(ex.Message, Is.EqualTo(
                $"Package 'big' serializes to {exactSize} bytes, which exceeds MongoDB's {Limit}-byte document limit."));
        });
    }

    private static PackageRevisionContentDocument Revision(params (string Name, long Size, string Content)[] files)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("shelly", "rev"),
            PackageName = "shelly",
            RevisionId = "rev",
            Files = files.ToDictionary(f => f.Name, f => new PackageFile { Content = f.Content, Size = f.Size })
        };
    }
}
