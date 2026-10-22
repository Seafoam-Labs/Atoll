namespace Atoll.Api.Services.Security;

public interface IPackageSecurityScanner
{
    int PolicyVersion { get; }

    ScanResult Scan(IReadOnlyDictionary<string, byte[]> files);
}