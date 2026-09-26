using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Persistence;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Persistence;

public class AurMetadataDeltaTests
{
    [Fact]
    public void Compute_EmptyPrevious_TreatsTheWholeSnapshotAsUpserts()
    {
        var snapshot = new[] { Meta("a"), Meta("b"), Meta("c") };

        var delta = AurMetadataDelta.Compute(Previous(), snapshot);

        Assert.Multiple(() =>
        {
            Assert.Equal(snapshot, delta.Upserts);
            Assert.Empty(delta.Removals);
            Assert.Equal(0, delta.Unchanged);
            Assert.False(delta.IsEmpty);
        });
    }

    [Fact]
    public void Compute_UnchangedSnapshot_IsEmpty()
    {
        // The two snapshots are separately constructed, so this only reads as unchanged when the
        // comparison is structural rather than reference based.
        var persisted = new[] { Meta("a"), Meta("b") };

        var delta = AurMetadataDelta.Compute(Previous(persisted), [Meta("a"), Meta("b")]);

        Assert.Multiple(() =>
        {
            Assert.True(delta.IsEmpty);
            Assert.Empty(delta.Upserts);
            Assert.Empty(delta.Removals);
            Assert.Equal(2, delta.Unchanged);
        });
    }

    [Fact]
    public void Compute_EmptySnapshot_RemovesEveryPersistedName()
    {
        var previous = new[] { Meta("a"), Meta("b") };

        var delta = AurMetadataDelta.Compute(Previous(previous), []);

        Assert.Multiple(() =>
        {
            Assert.Empty(delta.Upserts);
            Assert.Equal(["a", "b"], delta.Removals.Order(StringComparer.Ordinal), StringComparer.Ordinal);
            Assert.Equal(0, delta.Unchanged);
        });
    }

    [Fact]
    public void Compute_ChangedScalar_ProducesOneUpsertAndLeavesTheRestUntouched()
    {
        var persisted = Meta("a");
        var changed = Meta("b") with { Version = "2.0-1", NumVotes = 9 };

        var delta = AurMetadataDelta.Compute(Previous([persisted, Meta("b")]), [Meta("a"), changed]);

        Assert.Multiple(() =>
        {
            Assert.Equal([changed], delta.Upserts);
            Assert.Empty(delta.Removals);
            Assert.Equal(1, delta.Unchanged);
        });
    }

    [Theory]
    [InlineData(nameof(AurPackageMetadata.Depends))]
    [InlineData(nameof(AurPackageMetadata.MakeDepends))]
    [InlineData(nameof(AurPackageMetadata.OptDepends))]
    [InlineData(nameof(AurPackageMetadata.Conflicts))]
    [InlineData(nameof(AurPackageMetadata.Provides))]
    [InlineData(nameof(AurPackageMetadata.License))]
    [InlineData(nameof(AurPackageMetadata.Keywords))]
    [InlineData(nameof(AurPackageMetadata.CoMaintainers))]
    [InlineData(nameof(AurPackageMetadata.CheckDepends))]
    [InlineData(nameof(AurPackageMetadata.Groups))]
    [InlineData(nameof(AurPackageMetadata.Replaces))]
    public void Compute_ChangedCollectionField_ProducesOneUpsert(string field)
    {
        var persisted = Meta("a");
        var changed = WithCollection(Meta("a"), field, ["extra"]);

        var delta = AurMetadataDelta.Compute(Previous([persisted]), [changed]);

        Assert.Multiple(() =>
        {
            Assert.Equal([changed], delta.Upserts);
            Assert.Empty(delta.Removals);
            Assert.Equal(0, delta.Unchanged);
        });
    }

    [Fact]
    public void Compute_AddedName_ProducesOneUpsert()
    {
        var delta = AurMetadataDelta.Compute(Previous([Meta("a")]), [Meta("a"), Meta("b")]);

        Assert.Multiple(() =>
        {
            Assert.Equal("b", Assert.Single(delta.Upserts).Name);
            Assert.Empty(delta.Removals);
            Assert.Equal(1, delta.Unchanged);
        });
    }

    [Fact]
    public void Compute_VanishedName_ProducesOneRemoval()
    {
        var delta = AurMetadataDelta.Compute(Previous([Meta("a"), Meta("b")]), [Meta("a")]);

        Assert.Multiple(() =>
        {
            Assert.Empty(delta.Upserts);
            Assert.Equal(["b"], delta.Removals, StringComparer.Ordinal);
            Assert.Equal(1, delta.Unchanged);
        });
    }

    [Fact]
    public void Compute_PopularityOnlyChange_IsClassifiedAsChanged()
    {
        // Upstream rescales popularity across the corpus without touching LastModified, so a
        // LastModified gate would miss it and the persisted copy would rank on stale numbers.
        var persisted = Meta("a") with { Popularity = 12.5, LastModified = 1700000000 };

        var delta = AurMetadataDelta.Compute(
            Previous([persisted]),
            [Meta("a") with { Popularity = 10.84, LastModified = 1700000000 }]);

        var upsert = Assert.Single(delta.Upserts);

        Assert.Multiple(() =>
        {
            Assert.Equal(10.84, upsert.Popularity);
            Assert.Equal(persisted.LastModified, upsert.LastModified);
            Assert.Empty(delta.Removals);
            Assert.Equal(0, delta.Unchanged);
        });
    }

    [Fact]
    public void Compute_SameNameResubmittedUnderANewId_ProducesOneUpsertCarryingTheNewId()
    {
        // Upstream deletes and resubmits a package inside one window; name keying replaces the
        // document wholesale rather than reading as an add/remove pair.
        var persisted = Meta("a", id: 100);

        var delta = AurMetadataDelta.Compute(Previous([persisted]), [Meta("a", id: 900)]);

        Assert.Multiple(() =>
        {
            var upsert = Assert.Single(delta.Upserts);
            Assert.Empty(delta.Removals);
            Assert.Equal(900, upsert.Id);
            Assert.Equal("a", upsert.Name);
        });
    }

    [Fact]
    public void Compute_RepeatedNameInTheSnapshot_KeepsTheLastEntry()
    {
        var first = Meta("a") with { Version = "1.0-1" };
        var last = Meta("a") with { Version = "2.0-1" };

        var delta = AurMetadataDelta.Compute(Previous([first]), [first, last]);

        Assert.Multiple(() =>
        {
            // Two upserts for one id in an unordered bulk write would have no defined winner.
            Assert.Equal("2.0-1", Assert.Single(delta.Upserts).Version);
            Assert.Equal(0, delta.Unchanged);
        });
    }

    private static AurPackageMetadata Meta(string name, long id = 1)
    {
        return new AurPackageMetadata(
            id, name, id, name, "1.0-1",
            "sample metadata", null, 0, 0.5, null,
            null, null, 0, 0,
            "", [], [], [],
            [], [], [],
            [], []);
    }

    private static Dictionary<string, AurPackageMetadata> Previous(params AurPackageMetadata[] packages)
    {
        return packages.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    private static AurPackageMetadata WithCollection(
        AurPackageMetadata package, string field, IReadOnlyList<string> values)
    {
        return field switch
        {
            nameof(AurPackageMetadata.Depends) => package with { Depends = values },
            nameof(AurPackageMetadata.MakeDepends) => package with { MakeDepends = values },
            nameof(AurPackageMetadata.OptDepends) => package with { OptDepends = values },
            nameof(AurPackageMetadata.Conflicts) => package with { Conflicts = values },
            nameof(AurPackageMetadata.Provides) => package with { Provides = values },
            nameof(AurPackageMetadata.License) => package with { License = values },
            nameof(AurPackageMetadata.Keywords) => package with { Keywords = values },
            nameof(AurPackageMetadata.CoMaintainers) => package with { CoMaintainers = values },
            nameof(AurPackageMetadata.CheckDepends) => package with { CheckDepends = values },
            nameof(AurPackageMetadata.Groups) => package with { Groups = values },
            nameof(AurPackageMetadata.Replaces) => package with { Replaces = values },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a collection field.")
        };
    }
}
