namespace Atoll.Api.Services.Packages;

public interface IGitTransferService
{
    Task<GitTransferResult> AdvertiseRefsAsync(string name, Stream output, CancellationToken ct);

    Task<GitTransferResult> UploadPackAsync(string name, Stream input, Stream output, CancellationToken ct);
}

public abstract record GitTransferResult
{
    public sealed record Ok : GitTransferResult;

    public sealed record NotFound : GitTransferResult;
}