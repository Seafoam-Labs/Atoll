using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Packages;

public sealed record PackageTarball(string FileName, byte[] Bytes);

/// <summary>
///     Builds AUR-style .tar.gz package snapshots from stored revision content. Reads the package
///     repository rather than the bare-repository cache on purpose: pending, flagged, and error
///     revisions are deliberately excluded from materialized Git history, but downloads are an
///     ungated review surface (like the Blazor Files tab) and must serve them.
/// </summary>
public sealed class PackageTarballService(IPackageRepository repository)
{
    private const UnixFileMode RegularFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode ExecutableFileMode = RegularFileMode
        | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <param name="revisionId">
    ///     Optional <c>?rev=</c> pin. Unknown ids return null (404) rather than falling back to head -
    ///     a download must never silently deliver different bytes than requested.
    /// </param>
    public async Task<PackageTarball?> BuildAsync(
        string packageName,
        string? revisionId = null,
        CancellationToken ct = default)
    {
        var head = await repository.GetHeadAsync(packageName, ct);
        if (head is null)
            return null;

        var resolved = string.IsNullOrEmpty(revisionId) ? head.HeadRevisionId : revisionId;
        if (head.Revisions.TrueForAll(r => !string.Equals(r.RevisionId, resolved, StringComparison.Ordinal)))
            return null;

        var revision = await repository.GetRevisionAsync(packageName, resolved, ct);
        if (revision is null || revision.Files.Count == 0)
            return null;

        return new PackageTarball(FileName(packageName, resolved), Encode(packageName, revision));
    }

    // Tarballs should hand out the same exec bits a clone of the same revision materializes,
    // hence the shared heuristic with git materialization.
    private static byte[] Encode(string packageName, PackageRevisionContentDocument revision)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (path, file) in revision.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                using var content = new MemoryStream(Encoding.UTF8.GetBytes(file.Content));
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{packageName}/{path}")
                {
                    DataStream = content,
                    Mode = GitRepositoryCache.IsExecutable(path, file.Content)
                        ? ExecutableFileMode
                        : RegularFileMode,
                    ModificationTime = revision.CreatedAt
                };
                tar.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    private static string FileName(string packageName, string revisionId) =>
        $"{packageName}-{revisionId[..Math.Min(7, revisionId.Length)]}.tar.gz";
}
