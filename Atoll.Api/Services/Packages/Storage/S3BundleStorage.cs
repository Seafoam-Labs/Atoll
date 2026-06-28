using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Packages.Storage;

public sealed class S3BundleStorage(IAmazonS3 s3, IOptions<AtollOptions> options) : IBundleStorage
{
    private readonly string _bucket = options.Value.Storage.S3.Bucket;
    private readonly bool _chunked = options.Value.Storage.S3.ChunkedEncoding;

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = "packages/"
        }, cancellationToken);

        return response.S3Objects
            .Select(o => Path.GetFileNameWithoutExtension(o.Key["packages/".Length..]))
            .ToList();
    }

    public async Task<bool> ExistsAsync(string packageName, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(_bucket, BundleKey(packageName), cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DownloadAsync(string packageName, string destinationPath, CancellationToken cancellationToken = default)
    {
        var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket,
            Key = BundleKey(packageName)
        }, cancellationToken);

        await response.WriteResponseStreamToFileAsync(destinationPath, false, cancellationToken);
    }

    public async Task UploadAsync(string packageName, string sourcePath, CancellationToken cancellationToken = default)
    {
        await s3.PutObjectAsync(new PutObjectRequest
        {
            UseChunkEncoding = _chunked,
            BucketName = _bucket,
            Key = BundleKey(packageName),
            FilePath = sourcePath
        }, cancellationToken);
    }

    public async Task DeleteAsync(string packageName, CancellationToken cancellationToken = default)
    {
        await s3.DeleteObjectAsync(_bucket, BundleKey(packageName), cancellationToken);
    }

    private static string BundleKey(string packageName)
    {
        return $"packages/{packageName}.bundle";
    }
}