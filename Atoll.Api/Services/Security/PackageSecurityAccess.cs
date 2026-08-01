using Atoll.Api.Services.Packages;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Security;

public sealed record SecurityAccessResult(bool Allowed, string? ReasonCode)
{
    public static SecurityAccessResult Allow()
    {
        return new SecurityAccessResult(true, null);
    }

    public static SecurityAccessResult Block(string reasonCode)
    {
        return new SecurityAccessResult(false, reasonCode);
    }
}

public static class SecurityAccessReasonCodes
{
    public const string Pending = "security_status_pending";
    public const string Flagged = "security_status_flagged";
    public const string Error = "security_scan_error";
}

public sealed class PackageSecurityAccess(
    IPackageRepository packageRepository,
    IPackageSecurityRepository securityRepository,
    IOptions<AtollOptions> options)
    : IPackageSecurityAccess
{
    private readonly SecurityOptions _security = options.Value.Security;

    public async Task<SecurityAccessResult> CheckAsync(string packageName, CancellationToken ct = default)
    {
        if (!_security.Enabled)
            return SecurityAccessResult.Allow();

        if (!await packageRepository.ExistsAsync(packageName, ct))
            return SecurityAccessResult.Allow();

        var scan = await securityRepository.GetAsync(packageName, ct);
        return scan?.Status switch
        {
            SecurityStatus.Verified => SecurityAccessResult.Allow(),
            SecurityStatus.Pending or null => SecurityAccessResult.Block(SecurityAccessReasonCodes.Pending),
            SecurityStatus.Flagged => SecurityAccessResult.Block(SecurityAccessReasonCodes.Flagged),
            _ => SecurityAccessResult.Block(SecurityAccessReasonCodes.Error)
        };
    }
}