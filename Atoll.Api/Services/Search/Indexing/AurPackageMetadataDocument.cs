using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Atoll.Api.Services.Search.Indexing;

[BsonIgnoreExtraElements]
public sealed class AurPackageMetadataDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;

    [BsonElement("batch")]
    public string BatchId { get; set; } = null!;

    [BsonElement("aur_id")]
    public long AurId { get; set; }

    [BsonElement("name")] public string Name { get; set; } = null!;

    [BsonElement("package_base_id")] public long PackageBaseId { get; set; }

    [BsonElement("package_base")] public string PackageBase { get; set; } = null!;

    [BsonElement("version")] public string Version { get; set; } = null!;

    [BsonElement("description")] public string Description { get; set; } = null!;

    [BsonElement("url")] public string? Url { get; set; }

    [BsonElement("num_votes")] public long NumVotes { get; set; }

    [BsonElement("popularity")] public double Popularity { get; set; }

    [BsonElement("out_of_date")] public long? OutOfDate { get; set; }

    [BsonElement("maintainer")] public string? Maintainer { get; set; }

    [BsonElement("submitter")] public string? Submitter { get; set; }

    [BsonElement("first_submitted")] public long FirstSubmitted { get; set; }

    [BsonElement("last_modified")] public long LastModified { get; set; }

    [BsonElement("url_path")] public string UrlPath { get; set; } = null!;

    [BsonElement("depends")] public string[] Depends { get; set; } = null!;

    [BsonElement("make_depends")] public string[] MakeDepends { get; set; } = null!;

    [BsonElement("opt_depends")] public string[] OptDepends { get; set; } = null!;

    [BsonElement("conflicts")] public string[] Conflicts { get; set; } = null!;

    [BsonElement("provides")] public string[] Provides { get; set; } = null!;

    [BsonElement("license")] public string[] License { get; set; } = null!;

    [BsonElement("keywords")] public string[] Keywords { get; set; } = null!;

    [BsonElement("co_maintainers")] public string[] CoMaintainers { get; set; } = null!;

    public AurPackageMetadata ToMetadata()
    {
        return new AurPackageMetadata(
            AurId, Name, PackageBaseId, PackageBase, Version, Description,
            Url, NumVotes, Popularity, OutOfDate, Maintainer, Submitter,
            FirstSubmitted, LastModified, UrlPath,
            Depends, MakeDepends, OptDepends, Conflicts, Provides, License,
            Keywords, CoMaintainers
        );
    }
}