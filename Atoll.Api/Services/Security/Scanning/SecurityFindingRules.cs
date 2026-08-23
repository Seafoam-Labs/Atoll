namespace Atoll.Api.Services.Security.Scanning;

internal sealed record SecurityFindingRule(string Id, FindingSeverity Severity, string Description);

internal static class SecurityFindingRules
{
    public static readonly SecurityFindingRule LocalBinary = new(
        "local-binary",
        FindingSeverity.Critical,
        "Local source file '{0}' is {1} that cannot be reviewed as text - it may contain malicious code.");

    // Same rule id as LocalBinary, retained for review but non-blocking: the content is
    // recognized as inert by its magic bytes or file kind and cannot execute on its own.
    public static readonly SecurityFindingRule LocalBinaryInertMedia = new(
        "local-binary",
        FindingSeverity.Medium,
        "Local source file '{0}' is binary content that appears to be inert media or data - it cannot be reviewed as text, but it cannot execute on its own.");

    // Same rule id as LocalBinary, retained for review but non-blocking: the only binary
    // indicator is undecodable bytes (U+FFFD) - no NUL or control characters - so the file
    // is treated as text in an unrecognized encoding rather than unreviewable binary.
    public static readonly SecurityFindingRule LocalBinaryUnencodableText = new(
        "local-binary",
        FindingSeverity.Medium,
        "Local source file '{0}' contains text in an unrecognized encoding - parts of it cannot be reviewed, but it contains no binary control characters.");

    public static readonly SecurityFindingRule SuspiciousSourceUrl = new(
        "suspicious-source-url",
        FindingSeverity.Medium,
        "Source URL '{0}' points to a binary/archive that cannot be reviewed as text - it may contain malicious code.");

    public static readonly SecurityFindingRule NetworkToShell = new(
        "network-to-shell",
        FindingSeverity.Critical,
        "A download is piped directly into a shell - fetched code executes without any integrity check.");

    public static readonly SecurityFindingRule DecodeToShell = new(
        "decode-to-shell",
        FindingSeverity.Critical,
        "Encoded data is decoded and piped into a shell - the executed code cannot be reviewed.");

    public static readonly SecurityFindingRule EvalIndirection = new(
        "eval-indirection",
        FindingSeverity.Critical,
        "Dynamic command execution - a command is decoded/evaluated and run, so its real behavior cannot be reviewed.");

    public static readonly SecurityFindingRule CommandSubstitution = new(
        "command-substitution",
        FindingSeverity.Medium,
        "Dynamic command construction - the effective command is computed at runtime and cannot be statically resolved.");

    public static readonly SecurityFindingRule VariableIndirection = new(
        "variable-indirection",
        FindingSeverity.Medium,
        "Bash indirect variable expansion - the referenced variable is resolved at runtime and cannot be statically resolved.");

    public static readonly SecurityFindingRule WriteOutsideBuildRoot = new(
        "write-outside-build-root",
        FindingSeverity.High,
        "A write targets a path outside the build root - it can modify system files at build/install time.");

    public static readonly SecurityFindingRule NetworkExecution = new(
        "network-execution",
        FindingSeverity.High,
        "A download is executed or redirected into an interpreter - fetched code runs outside pacman's control.");

    public static readonly SecurityFindingRule HiddenCharacter = new(
        "hidden-character",
        FindingSeverity.Critical,
        "Hidden or bidirectional control characters detected - the visible code may not match what the shell actually executes.");

    public static readonly SecurityFindingRule PrivilegeEscalation = new(
        "privilege-escalation",
        FindingSeverity.High,
        "Privilege escalation tool '{0}' is invoked - this runs code as root outside of the package manager's control and can give the package unrestricted access to the whole system.");

    public static readonly SecurityFindingRule RiskyTool = new(
        "risky-tool",
        FindingSeverity.Medium,
        "'{0}' is invoked - this fetches/executes external code outside pacman's control.");

    public static readonly SecurityFindingRule Homograph = new(
        "homograph",
        FindingSeverity.High,
        "A package metadata value contains suspicious non-ASCII characters - it may spoof a trusted name or hide content (homograph attack).");
}