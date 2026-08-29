using Atoll.Api.Services.Security.Scanning;

namespace Atoll.Api.Services.Security;

public sealed class PkgBuildSecurityScanner : IPackageSecurityScanner
{
    // Increment whenever a scanner rule change requires persisted verdicts to be refreshed.
    public const int CurrentPolicyVersion = 3;

    public int PolicyVersion => CurrentPolicyVersion;

    public ScanResult Scan(IReadOnlyDictionary<string, string> files)
    {
        var findings = new List<SecurityFinding>();
        var pkgBuildText = files.FirstOrDefault(f => PackageBuildFileClassifier.IsPkgbuild(f.Key)).Value;

        foreach (var (path, content) in files)
        {
            var binaryFinding = LocalSourceBinaryScanner.Scan(content, path);
            if (binaryFinding is not null)
                findings.Add(binaryFinding);

            if (string.IsNullOrWhiteSpace(content) || !PackageBuildFileClassifier.IsScannable(path))
                continue;

            findings.AddRange(ShellContentScanner.Scan(content, path, IsReferencedByPkgBuild(path, pkgBuildText)));

            if (PackageBuildFileClassifier.IsPkgbuild(path))
            {
                findings.AddRange(PkgBuildSourceUrlScanner.Scan(content, path));
                findings.AddRange(HomographScanner.Scan(content, path));
            }
        }

        var status = findings.Any(f => f.Severity is FindingSeverity.Critical or FindingSeverity.High)
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