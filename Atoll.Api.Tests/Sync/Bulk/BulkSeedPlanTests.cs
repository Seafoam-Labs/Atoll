using Atoll.Api.Services.Sync.Bulk;
using Xunit;

namespace Atoll.Api.Tests.Sync.Bulk;

public class BulkSeedPlanTests
{
    [Fact]
    public void BuildPkgBaseTargets_SplitPackages_MapToSingleBaseKey()
    {
        // Split packages: two pkgnames share one pkgbase branch.
        var targets = BulkSeedPlan.BuildPkgBaseTargets(["libfoo", "libfoo-devel"], _ => "foo");

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "foo" }, targets.Keys, strict: true);
            Assert.Equivalent(new[] { "libfoo", "libfoo-devel" }, targets["foo"], strict: true);
        });
    }

    [Fact]
    public void BuildPkgBaseTargets_MultiplePkgNamesPerBase_DeduplicatesToSingleEntry()
    {
        var targets = BulkSeedPlan.BuildPkgBaseTargets(
            ["libfoo", "libfoo-devel", "shelly", "libfoo-docs"],
            name => name.StartsWith("libfoo", StringComparison.Ordinal) ? "foo" : name);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "foo", "shelly" }, targets.Keys, strict: true);
            Assert.Equivalent(new[] { "libfoo", "libfoo-devel", "libfoo-docs" }, targets["foo"], strict: true);
            Assert.Equivalent(new[] { "shelly" }, targets["shelly"], strict: true);
        });
    }

    [Fact]
    public void BuildPkgBaseTargets_EmptyResolverResult_FallsBackToPkgName()
    {
        // Cold start / stale snapshot: empty pkgbase means non-split, use pkgname.
        var targets = BulkSeedPlan.BuildPkgBaseTargets(["shelly", "other"], _ => "");

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "shelly", "other" }, targets.Keys, strict: true);
            Assert.Equivalent(new[] { "shelly" }, targets["shelly"], strict: true);
        });
    }

    [Fact]
    public void BuildPkgBaseTargets_NamesWithinOneBase_KeepInputOrder()
    {
        var targets = BulkSeedPlan.BuildPkgBaseTargets(["zeta", "alpha", "beta"], _ => "shared");

        Assert.Equal(new[] { "zeta", "alpha", "beta" }, targets["shared"]);
    }

    [Fact]
    public void BuildPkgBaseTargets_EmptyNames_AreSkipped()
    {
        var targets = BulkSeedPlan.BuildPkgBaseTargets(["", "shelly", ""], name => name);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "shelly" }, targets.Keys, strict: true);
            Assert.Equivalent(new[] { "shelly" }, targets["shelly"], strict: true);
        });
    }

    [Fact]
    public void ChunkBy_NonDivisibleSource_SplitsIntoExpectedSizes()
    {
        var batch = BulkSeedPlan.ChunkBy([.. Enumerable.Range(0, 7)], 3).ToList();

        Assert.Multiple(() =>
        {
            Assert.Equal(3, batch.Count);
            Assert.Equal(new[] { 0, 1, 2 }, batch[0]);
            Assert.Equal(new[] { 3, 4, 5 }, batch[1]);
            Assert.Equal(new[] { 6 }, batch[2]);
        });
    }

    [Fact]
    public void ChunkBy_EmptySource_YieldsNothing()
    {
        var batch = BulkSeedPlan.ChunkBy(Array.Empty<int>(), 10).ToList();

        Assert.Empty(batch);
    }

    [Fact]
    public void ChunkBy_BatchLargerThanSource_ReturnsSingleFullSlice()
    {
        var batch = BulkSeedPlan.ChunkBy([1, 2], 100).ToList();

        Assert.Multiple(() =>
        {
            Assert.Equal(new[] { 1, 2 }, Assert.Single(batch));
        });
    }

    [Fact]
    public void ChunkBy_NonPositiveSize_Throws()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BulkSeedPlan.ChunkBy([1], 0).ToList());
            Assert.Throws<ArgumentOutOfRangeException>(() => BulkSeedPlan.ChunkBy([1], -1).ToList());
        });
    }
}