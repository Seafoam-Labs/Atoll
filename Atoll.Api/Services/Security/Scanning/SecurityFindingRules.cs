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

    // Same rule id as LocalBinary, retained for review but non-blocking. A committed
    // archive is more auditable than a remote one fetched at build time (which the
    // suspicious-source-url rule rates Medium), so blocking on it blocks far more
    // legitimate packages (vendored assets, source snapshots) than malicious ones.
    public static readonly SecurityFindingRule LocalBinaryArchive = new(
        "local-binary",
        FindingSeverity.Medium,
        "Local source file '{0}' is a binary archive - its contents cannot be reviewed as text, but it cannot execute on its own and is versioned in the repository.");

    // Same rule id as LocalBinary, retained for review but non-blocking: the only binary
    // indicator is undecodable bytes (U+FFFD) - no NUL or control characters - so the file
    // is treated as text in an unrecognized encoding rather than unreviewable binary.
    public static readonly SecurityFindingRule LocalBinaryUnencodableText = new(
        "local-binary",
        FindingSeverity.Medium,
        "Local source file '{0}' contains text in an unrecognized encoding - parts of it cannot be reviewed, but it contains no binary control characters.");

    // Same rule id as LocalBinary, retained for review but non-blocking: binary content
    // that matches no recognized executable format (ELF, PE) cannot run on its own, and
    // anything the PKGBUILD does with it is covered by the shell-level rules.
    public static readonly SecurityFindingRule LocalBinaryData = new(
        "local-binary",
        FindingSeverity.Medium,
        "Local source file '{0}' is binary data that cannot be reviewed as text - no executable format was recognized, so it cannot execute on its own.");

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

    // Same rule id as EvalIndirection, retained for review but non-blocking: the evaluated
    // text is built from literal words plus tilde/variable expansion (eval echo ~$SUDO_USER),
    // or comes from the output of well-known environment emitters and local file parsers -
    // the same trust level as ordinary command substitution, which is rated Medium.
    public static readonly SecurityFindingRule EvalIndirectionComputed = new(
        "eval-indirection",
        FindingSeverity.Medium,
        "Dynamic command execution - the command is computed at runtime, but it is built from reviewable literal text or the output of local, well-known tools.");

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

    // Same rule id as WriteOutsideBuildRoot, retained for review but non-blocking: alpm
    // runs .install scriptlets as root as part of the transaction, so writing system files
    // from one (/etc/shells entries, config files, generated keys) is the ordinary job of a
    // scriptlet, not an escape from the build sandbox.
    public static readonly SecurityFindingRule WriteOutsideBuildRootScriptlet = new(
        "write-outside-build-root",
        FindingSeverity.Medium,
        "A write targets a path outside the build root from the package's .install scriptlet - scriptlets run as root under alpm's control, and writing system files is their ordinary purpose.");

    public static readonly SecurityFindingRule NetworkExecution = new(
        "network-execution",
        FindingSeverity.High,
        "A download is executed or redirected into an interpreter - fetched code runs outside alpm's control.");

    public static readonly SecurityFindingRule HiddenCharacter = new(
        "hidden-character",
        FindingSeverity.Critical,
        "Hidden or bidirectional control characters detected - the visible code may not match what the shell actually executes.");

    // Same rule id as HiddenCharacter, retained for review but non-blocking: zero-width
    // characters are inert inside words - the shell never treats them as separators - so
    // they cannot make executed code differ from the reviewed code. They only make names
    // display differently from how they read (emoji joiners, Persian orthography,
    // copy-paste artifacts).
    public static readonly SecurityFindingRule HiddenCharacterZeroWidth = new(
        "hidden-character",
        FindingSeverity.Medium,
        "Zero-width characters detected - they cannot change how the shell parses the line, but names may not read exactly as they display.");

    public static readonly SecurityFindingRule PrivilegeEscalation = new(
        "privilege-escalation",
        FindingSeverity.High,
        "Privilege escalation tool '{0}' is invoked - this runs code as root outside of the package manager's control and can give the package unrestricted access to the whole system.");

    // Same rule id as PrivilegeEscalation, retained for review but non-blocking: alpm
    // already runs .install scriptlets as root, so invoking sudo/doas/su inside one cannot
    // escalate anything - it is a redundant invocation, not code running outside the
    // package manager's control.
    public static readonly SecurityFindingRule PrivilegeEscalationScriptlet = new(
        "privilege-escalation",
        FindingSeverity.Medium,
        "Privilege escalation tool '{0}' is invoked inside the package's .install scriptlet - scriptlets already run as root under alpm's control, so the call is redundant rather than an escalation.");

    // Same rule id as PrivilegeEscalation, retained for review but non-blocking: helper
    // scripts ship inside the package and only run when the user invokes them voluntarily,
    // typically with root already granted, so the tool confers nothing the user did not
    // hand over. Writes to system files from such scripts stay covered by
    // write-outside-build-root, which is not downgraded for helper scripts.
    public static readonly SecurityFindingRule PrivilegeEscalationHelperScript = new(
        "privilege-escalation",
        FindingSeverity.Medium,
        "Privilege escalation tool '{0}' is invoked inside a packaged helper script - helper scripts only run when the user invokes them voluntarily, typically with the privileges they already hold.");

    public static readonly SecurityFindingRule RiskyTool = new(
        "risky-tool",
        FindingSeverity.Medium,
        "'{0}' is invoked - this fetches/executes external code outside alpm's control.");

    // Medium, not blocking: the corpus shows homograph matches in AUR metadata are benign
    // (real IDN domains, encoding artifacts) rather than spoofing attacks, and the mirror
    // displays the raw values, so a user can see the exact characters before building.
    // The finding is kept for review so genuine lookalike hosts remain visible.
    public static readonly SecurityFindingRule Homograph = new(
        "homograph",
        FindingSeverity.Medium,
        "A package metadata value contains suspicious non-ASCII characters - it may spoof a trusted name or hide content (homograph attack).");
}