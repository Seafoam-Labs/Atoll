namespace Atoll.Api.Services.Security;

public interface IPackageSecurityAccess
{
    Task<SecurityAccessResult> CheckAsync(string packageName, CancellationToken ct = default);
}