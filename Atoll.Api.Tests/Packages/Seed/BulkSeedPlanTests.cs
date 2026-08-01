using Atoll.Api.Services.Packages.Seed;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages.Seed;

public class BulkSeedPlanTests
{
    [Test]
    public void BuildPkgBaseTargets_maps_split_packages_to_single_base()
    {
        // Split packages: two pkgnames share one pkgbase branch.
        var targets = BulkSeedPlan.BuildPkgBaseTargets(["libfoo", "libfoo-devel"], _ => "foo");

        Assert.Multiple(() =>
        {
            Assert.That(targets.Keys, Is.EquivalentTo(["foo"]));
            Assert.That(targets["foo"], Is.EquivalentTo(["libfoo", "libfoo-devel"]));
        });
    }

    [Test]
    public void BuildPkgBaseTargets_dedupes_when_multiple_pkgnames_map_to_same_base()
    {
        var targets = BulkSeedPlan.BuildPkgBaseTargets(
            ["libfoo", "libfoo-devel", "shelly", "libfoo-docs"],
            name => name.StartsWith("libfoo", StringComparison.Ordinal) ? "foo" : name);

        Assert.Multiple(() =>
        {
            Assert.That(targets.Keys, Is.EquivalentTo(["foo", "shelly"]));
            Assert.That(targets["foo"], Is.EquivalentTo(["libfoo", "libfoo-devel", "libfoo-docs"]));
            Assert.That(targets["shelly"], Is.EquivalentTo(["shelly"]));
        });
    }

    [Test]
    public void BuildPkgBaseTargets_falls_back_to_pkgname_when_resolver_returns_empty()
    {
        // Cold start / stale snapshot: empty pkgbase means non-split, use pkgname.
        var targets = BulkSeedPlan.BuildPkgBaseTargets(["shelly", "other"], _ => "");

        Assert.Multiple(() =>
        {
            Assert.That(targets.Keys, Is.EquivalentTo(["shelly", "other"]));
            Assert.That(targets["shelly"], Is.EquivalentTo(["shelly"]));
        });
    }

    [Test]
    public void BuildPkgBaseTargets_preserves_input_order_within_a_base()
    {
        var targets = BulkSeedPlan.BuildPkgBaseTargets(["zeta", "alpha", "beta"], _ => "shared");

        Assert.That(targets["shared"], Is.EqualTo(["zeta", "alpha", "beta"]));
    }

    [Test]
    public void BuildPkgBaseTargets_skips_empty_names()
    {
        var targets = BulkSeedPlan.BuildPkgBaseTargets(["", "shelly", ""], name => name);

        Assert.Multiple(() =>
        {
            Assert.That(targets.Keys, Is.EquivalentTo(["shelly"]));
            Assert.That(targets["shelly"], Is.EquivalentTo(["shelly"]));
        });
    }

    [Test]
    public void ChunkBy_splits_into_expected_sizes()
    {
        var batch = BulkSeedPlan.ChunkBy(Enumerable.Range(0, 7).ToArray(), 3).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(batch, Has.Count.EqualTo(3));
            Assert.That(batch[0], Is.EqualTo([0, 1, 2]));
            Assert.That(batch[1], Is.EqualTo([3, 4, 5]));
            Assert.That(batch[2], Is.EqualTo([6]));
        });
    }

    [Test]
    public void ChunkBy_empty_source_yields_nothing()
    {
        var batch = BulkSeedPlan.ChunkBy(Array.Empty<int>(), 10).ToList();

        Assert.That(batch, Is.Empty);
    }

    [Test]
    public void ChunkBy_batch_larger_than_source_returns_single_full_slice()
    {
        var batch = BulkSeedPlan.ChunkBy([1, 2], 100).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(batch, Has.Count.EqualTo(1));
            Assert.That(batch[0], Is.EqualTo([1, 2]));
        });
    }

    [Test]
    public void ChunkBy_non_positive_size_throws()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BulkSeedPlan.ChunkBy([1], 0).ToList());
            Assert.Throws<ArgumentOutOfRangeException>(() => BulkSeedPlan.ChunkBy([1], -1).ToList());
        });
    }
}