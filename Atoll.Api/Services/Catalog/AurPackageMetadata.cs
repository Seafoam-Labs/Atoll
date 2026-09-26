namespace Atoll.Api.Services.Catalog;

public sealed record AurPackageMetadata(
    long Id,
    string Name,
    long PackageBaseId,
    string PackageBase,
    string Version,
    string Description,
    string? Url,
    long NumVotes,
    double Popularity,
    long? OutOfDate,
    string? Maintainer,
    string? Submitter,
    long FirstSubmitted,
    long LastModified,
    string UrlPath,
    IReadOnlyList<string> Depends,
    IReadOnlyList<string> MakeDepends,
    IReadOnlyList<string> OptDepends,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> License,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> CoMaintainers
)
{
    public IReadOnlyList<string> CheckDepends { get; init; } = [];
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<string> Replaces { get; init; } = [];

    // The generated record equality compares the list properties by reference, so two structurally
    // identical packages parsed from two different downloads of the same dump never compare equal.
    // Content equality is what tells an unchanged package apart from one worth persisting again.
    public bool Equals(AurPackageMetadata? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        return Id == other.Id
               && string.Equals(Name, other.Name, StringComparison.Ordinal)
               && PackageBaseId == other.PackageBaseId
               && string.Equals(PackageBase, other.PackageBase, StringComparison.Ordinal)
               && string.Equals(Version, other.Version, StringComparison.Ordinal)
               && string.Equals(Description, other.Description, StringComparison.Ordinal)
               && string.Equals(Url, other.Url, StringComparison.Ordinal)
               && NumVotes == other.NumVotes
               && Popularity.Equals(other.Popularity)
               && OutOfDate == other.OutOfDate
               && string.Equals(Maintainer, other.Maintainer, StringComparison.Ordinal)
               && string.Equals(Submitter, other.Submitter, StringComparison.Ordinal)
               && FirstSubmitted == other.FirstSubmitted
               && LastModified == other.LastModified
               && string.Equals(UrlPath, other.UrlPath, StringComparison.Ordinal)
               && SameSequence(Depends, other.Depends)
               && SameSequence(MakeDepends, other.MakeDepends)
               && SameSequence(OptDepends, other.OptDepends)
               && SameSequence(Conflicts, other.Conflicts)
               && SameSequence(Provides, other.Provides)
               && SameSequence(License, other.License)
               && SameSequence(Keywords, other.Keywords)
               && SameSequence(CoMaintainers, other.CoMaintainers)
               && SameSequence(CheckDepends, other.CheckDepends)
               && SameSequence(Groups, other.Groups)
               && SameSequence(Replaces, other.Replaces);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(PackageBaseId);
        hash.Add(PackageBase, StringComparer.Ordinal);
        hash.Add(Version, StringComparer.Ordinal);
        hash.Add(Description, StringComparer.Ordinal);
        hash.Add(Url, StringComparer.Ordinal);
        hash.Add(NumVotes);
        hash.Add(Popularity);
        hash.Add(OutOfDate);
        hash.Add(Maintainer, StringComparer.Ordinal);
        hash.Add(Submitter, StringComparer.Ordinal);
        hash.Add(FirstSubmitted);
        hash.Add(LastModified);
        hash.Add(UrlPath, StringComparer.Ordinal);
        AddSequence(ref hash, Depends);
        AddSequence(ref hash, MakeDepends);
        AddSequence(ref hash, OptDepends);
        AddSequence(ref hash, Conflicts);
        AddSequence(ref hash, Provides);
        AddSequence(ref hash, License);
        AddSequence(ref hash, Keywords);
        AddSequence(ref hash, CoMaintainers);
        AddSequence(ref hash, CheckDepends);
        AddSequence(ref hash, Groups);
        AddSequence(ref hash, Replaces);

        return hash.ToHashCode();
    }

    private static bool SameSequence(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static void AddSequence(ref HashCode hash, IReadOnlyList<string> values)
    {
        foreach (var value in values) hash.Add(value, StringComparer.Ordinal);
        // The count terminates the run so two sequences sharing a prefix cannot collide.
        hash.Add(values.Count);
    }
}
