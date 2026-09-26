using Atoll.Api.Services.Packages;
using MongoDB.Bson;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Packages;

public class PackageDocumentSizeValidatorTests
{
    private const long Limit = PackageDocumentSizeValidator.MongoMaxDocumentSizeBytes;

    [Fact]
    public void Validate_ConservativeEstimateBelowLimit_Accepts()
    {
        var revision = Revision(("PKGBUILD", 200, "pkgname=shelly\n"));

        PackageDocumentSizeValidator.Validate("shelly", revision);
    }

    [Fact]
    public void Validate_EstimateExceedsLimitButExactBsonFits_Accepts()
    {
        // Declared sizes push the conservative estimate past 16 MiB while the actual
        // content stays tiny, so the exact measurement must accept the document.
        var revision = Revision(
            ("large-1.txt", 9_000_000, "x"),
            ("large-2.txt", 9_000_000, "y"));

        PackageDocumentSizeValidator.Validate("shelly", revision);
    }

    [Fact]
    public void Validate_AtExactEstimateBoundary_SkipsExactMeasurement()
    {
        // size + 1-byte name + 160 + 1024 == 16 MiB exactly: the estimate check is
        // inclusive, so validation passes without ever serializing the document.
        var sizeAtBoundary = 16 * 1024 * 1024 - 1024 - 160 - 1;
        var revision = Revision(("a", sizeAtBoundary, "x"));

        PackageDocumentSizeValidator.Validate("shelly", revision);
    }

    [Fact]
    public void Validate_ExactBsonExceedsLimit_Rejects()
    {
        var revision = Revision(("huge.txt", 16 * 1024 * 1024, new string('a', 16 * 1024 * 1024)));

        var ex = Assert.Throws<PackageDocumentTooLargeException>(
            () => PackageDocumentSizeValidator.Validate("big", revision))!;

        var exactSize = revision.ToBson().LongLength;
        Assert.Multiple(() =>
        {
            Assert.Equal("big", ex.PackageName);
            Assert.Equal(exactSize, ex.SerializedSizeBytes);
            Assert.Equal(Limit, ex.MaxDocumentSizeBytes);
            Assert.Equal(
                $"Package 'big' serializes to {exactSize} bytes, which exceeds MongoDB's {Limit}-byte document limit.",
                ex.Message);
        });
    }

    private static PackageRevisionContentDocument Revision(params (string Name, long Size, string Content)[] files)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("shelly", "rev"),
            PackageName = "shelly",
            RevisionId = "rev",
            Files = files.ToDictionary(f => f.Name, f => new PackageFile { Content = f.Content, Size = f.Size }, StringComparer.Ordinal)
        };
    }
}
