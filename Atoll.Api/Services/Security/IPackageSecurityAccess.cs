namespace Atoll.Api.Services.Security;

public interface IPackageSecurityAccess
{
    Task<SecurityAccessResult> CheckAsync(string packageName, string? revisionId = null, CancellationToken ct = default);
}