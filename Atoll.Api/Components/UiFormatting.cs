using Atoll.Api.Services.Security;

namespace Atoll.Api.Components;

/// <summary>Shared CSS-class and formatting helpers for the UI pages.</summary>
public static class UiFormatting
{
    public static string BadgeClass(SecurityStatus status)
    {
        return status switch
        {
            SecurityStatus.Verified => "badge-verified",
            SecurityStatus.Pending => "badge-pending",
            SecurityStatus.Flagged => "badge-flagged",
            SecurityStatus.Error => "badge-error",
            _ => ""
        };
    }

    public static string BannerClass(SecurityStatus status)
    {
        return status switch
        {
            SecurityStatus.Verified => "status-banner-verified",
            SecurityStatus.Pending => "status-banner-pending",
            SecurityStatus.Flagged => "status-banner-flagged",
            SecurityStatus.Error => "status-banner-error",
            _ => ""
        };
    }

    public static string BannerClass(string? reasonCode)
    {
        return reasonCode switch
        {
            SecurityAccessReasonCodes.Flagged => "status-banner-flagged",
            SecurityAccessReasonCodes.Error => "status-banner-error",
            _ => "status-banner-pending"
        };
    }

    public static string BannerTitle(string? reasonCode)
    {
        return reasonCode switch
        {
            SecurityAccessReasonCodes.Flagged => "Flagged",
            SecurityAccessReasonCodes.Error => "Scan error",
            _ => "Pending"
        };
    }

    public static string BannerBody(string? reasonCode)
    {
        return reasonCode switch
        {
            SecurityAccessReasonCodes.Flagged => "— the scan found critical/high severity issues, so content and Git access are gated.",
            SecurityAccessReasonCodes.Error => "— the scan failed, so content and Git access stay blocked until a successful re-scan.",
            _ => "— the head revision is queued for scanning or being scanned right now."
        };
    }

    public static string StatusSummary(SecurityStatus status)
    {
        return status switch
        {
            SecurityStatus.Verified => "no high/critical red flags — content is served.",
            SecurityStatus.Flagged => "critical/high findings — content and Git access are gated.",
            SecurityStatus.Pending => "scan queued or in progress — content is blocked until it completes.",
            SecurityStatus.Error => "the scan failed — content stays blocked until a successful re-scan.",
            _ => ""
        };
    }

    public static string SeverityCountSummary(IReadOnlyCollection<SecurityFinding> findings)
    {
        if (findings.Count == 0) return "none";

        return string.Join(", ",
            findings
                .GroupBy(finding => finding.Severity)
                .OrderByDescending(group => group.Key)
                .Select(group => $"{group.Count()} {group.Key.ToString().ToLowerInvariant()}"));
    }

    public static string FindingCardClass(FindingSeverity severity)
    {
        return severity switch
        {
            FindingSeverity.Critical => "finding-card-critical",
            FindingSeverity.High => "finding-card-high",
            FindingSeverity.Medium => "finding-card-medium",
            FindingSeverity.Low => "finding-card-low",
            _ => "finding-card-info"
        };
    }

    public static string SeverityBadgeClass(FindingSeverity severity)
    {
        return severity switch
        {
            FindingSeverity.Critical => "badge-flagged",
            FindingSeverity.High => "badge-flagged",
            FindingSeverity.Medium => "badge-stale",
            FindingSeverity.Low => "badge-seeded",
            _ => ""
        };
    }

    public static string Truncate(string value)
    {
        return value.Length <= 12 ? value : value[..12];
    }

    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} kB",
            _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
        };
    }

    public static string FormatUnix(long unixSeconds, string format = "yyyy-MM-dd")
    {
        return unixSeconds <= 0
            ? "—"
            : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString(format);
    }
}
