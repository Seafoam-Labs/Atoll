namespace Atoll.Api.Services.Security.Scanning;

internal static class PackageBuildFileClassifier
{
    private static readonly string[] ScriptExtensions =
    [
        ".sh", ".bash", ".install", ".hook", ".py", ".pl", ".rb", ".service", ".csh", ".zsh"
    ];

    private static readonly string[] HelperScriptExtensions = [".sh", ".bash", ".csh", ".zsh"];

    public static bool IsPkgbuild(string path)
    {
        return Path.GetFileName(path).Equals("PKGBUILD", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInstallScriptlet(string path)
    {
        return Path.GetExtension(Path.GetFileName(path)).Equals(".install", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHelperScript(string path)
    {
        return HelperScriptExtensions.Contains(Path.GetExtension(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsScannable(string path)
    {
        if (IsPkgbuild(path))
            return true;

        return ScriptExtensions.Contains(Path.GetExtension(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase);
    }
}