using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security.Scanning;

namespace Atoll.Api.Services.Security;

public sealed class PkgBuildSecurityScanner : IPackageSecurityScanner
{
    // Increment whenever a scanner rule change requires persisted verdicts to be refreshed.
    // Version 5 scans raw bytes for magic detection instead of the UTF-8-decoded text.
    public const int CurrentPolicyVersion = 5;

    public int PolicyVersion => CurrentPolicyVersion;

    public ScanResult Scan(IReadOnlyDictionary<string, byte[]> files)
    {
        var findings = new List<SecurityFinding>();

        // Decode each file exactly once: the binary classifier and the shell rules must see
        // the same scan-only text view, and the PKGBUILD lookup must reuse it rather than
        // decoding the file a second time.
        var decoded = new List<(string Path, byte[] Bytes, string Text)>(files.Count);
        foreach (var (path, bytes) in files)
            decoded.Add((path, bytes, PackageText.DecodeForScanning(bytes)));

        var pkgBuildText = decoded
            .Where(file => PackageBuildFileClassifier.IsPkgbuild(file.Path))
            .Select(file => file.Text)
            .FirstOrDefault();

        foreach (var (path, bytes, text) in decoded)
        {
            var binaryFinding = LocalSourceBinaryScanner.Scan(bytes, text, path);
            if (binaryFinding is not null)
                findings.Add(binaryFinding);

            if (string.IsNullOrWhiteSpace(text) || !PackageBuildFileClassifier.IsScannable(path))
                continue;

            findings.AddRange(ShellContentScanner.Scan(text, path, IsReferencedByPkgBuild(path, pkgBuildText)));

            if (PackageBuildFileClassifier.IsPkgbuild(path))
            {
                findings.AddRange(PkgBuildSourceUrlScanner.Scan(text, path));
                findings.AddRange(HomographScanner.Scan(text, path));
            }
        }

        var status = findings.Exists(f => f.Severity is FindingSeverity.Critical or FindingSeverity.High)
            ? SecurityStatus.Flagged
            : SecurityStatus.Verified;

        return new ScanResult(status, findings);
    }

    /// <summary>
    ///     True when the PKGBUILD can invoke this file: the file is the PKGBUILD itself, or
    ///     the PKGBUILD mentions it somewhere other than a data declaration (a source or
    ///     checksum array entry) or a staging copy into the build tree (into
    ///     <c>$pkgdir</c>, or at a relative destination) - an invocation, an
    ///     <c>install=</c> entry, a transport to a system path. A script the PKGBUILD never
    ///     invokes cannot run during build or install, so its findings are downgraded to
    ///     review-only; without a PKGBUILD there is no reference to check and the
    ///     conservative answer is "referenced".
    /// </summary>
    private static bool IsReferencedByPkgBuild(string path, string? pkgBuildText)
    {
        return PackageBuildFileClassifier.IsPkgbuild(path) ||
               pkgBuildText is null ||
               PkgBuildScriptReferences.IsInvoked(Path.GetFileName(path), pkgBuildText);
    }
}