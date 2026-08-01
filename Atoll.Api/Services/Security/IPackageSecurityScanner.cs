namespace Atoll.Api.Services.Security;

public interface IPackageSecurityScanner
{
    ScanResult Scan(IReadOnlyDictionary<string, string> files);
}