namespace Atoll.Api.Services.Git;

/// <summary>
///     Git Smart HTTP protocol vocabulary for the read-only clone surface.
/// </summary>
public static class GitSmartHttp
{
    public const string UploadPackService = "git-upload-pack";

    public const string AdvertisementMediaType = "application/x-git-upload-pack-advertisement";

    public const string UploadPackResultMediaType = "application/x-git-upload-pack-result";

    /// <summary>git clients cache advertisements aggressively; a mirror must not let them.</summary>
    public const string NoCacheControl = "no-cache, max-age=0, must-revalidate";

    /// <summary>
    ///     Only the fetch service is served: accepting <c>git-receive-pack</c> would turn the
    ///     mirror into an unauthenticated push target.
    /// </summary>
    public static bool IsSupportedService(string? service) =>
        string.Equals(service, UploadPackService, StringComparison.Ordinal);
}
