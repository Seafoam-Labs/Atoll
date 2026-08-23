using Atoll.Api.Services.Security.Scanning;

namespace Atoll.Api.Services.Security;

public sealed class PkgBuildSecurityScanner : IPackageSecurityScanner
{
    public ScanResult Scan(IReadOnlyDictionary<string, string> files)
    {
        var findings = new List<SecurityFinding>();

        foreach (var (path, content) in files)
        {
            var binaryFinding = LocalSourceBinaryScanner.Scan(content, path);
            if (binaryFinding is not null)
                findings.Add(binaryFinding);

            if (string.IsNullOrWhiteSpace(content) || !PackageBuildFileClassifier.IsScannable(path))
                continue;

            findings.AddRange(ShellContentScanner.Scan(content, path));

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
}