namespace Atoll.Api.Services.Security.Scanning;

internal static class LocalSourceBinaryScanner
{
    public static SecurityFinding? Scan(string content, string path)
    {
        var isElf = content.StartsWith("\u007fELF", StringComparison.Ordinal);
        if (!isElf && !HasBinaryCharacters(content))
            return null;

        var (article, kind) = isElf ? ("an", "ELF executable") : ("a", "binary");
        return new SecurityFinding(
            "local-binary",
            FindingSeverity.Critical,
            $"Local source file '{path}' is {article} {kind} that cannot be reviewed as text - it may contain malicious code.",
            path,
            path);
    }

    private static bool HasBinaryCharacters(string content)
    {
        foreach (var character in content)
        {
            if (character is '\uFFFD' or '\0')
                return true;

            if (char.IsControl(character) && character is not ('\n' or '\r' or '\t' or '\v' or '\f'))
                return true;
        }

        return false;
    }
}