namespace Atoll.Api.Services.Search.Indexing;

public static class AurMetadataExtensions
{
    public static AurPackageMetadataDocument ToDocument(this AurPackageMetadata p, string batchId)
    {
        return new AurPackageMetadataDocument
        {
            BatchId = batchId,
            AurId = p.Id,
            Name = p.Name,
            PackageBaseId = p.PackageBaseId,
            PackageBase = p.PackageBase,
            Version = p.Version,
            Description = p.Description,
            Url = p.Url,
            NumVotes = p.NumVotes,
            Popularity = p.Popularity,
            OutOfDate = p.OutOfDate,
            Maintainer = p.Maintainer,
            Submitter = p.Submitter,
            FirstSubmitted = p.FirstSubmitted,
            LastModified = p.LastModified,
            UrlPath = p.UrlPath,
            Depends = [.. p.Depends],
            MakeDepends = [.. p.MakeDepends],
            OptDepends = [.. p.OptDepends],
            Conflicts = [.. p.Conflicts],
            Provides = [.. p.Provides],
            License = [.. p.License],
            Keywords = [.. p.Keywords],
            CoMaintainers = [.. p.CoMaintainers]
        };
    }
}