using System.Security.Cryptography;
using System.Text;

namespace Atoll.Api.Services.Git;

/// <summary>One side-by-side file to render. A null side means the file is absent there.</summary>
public sealed record TextDiffEntry(string Path, string? OldText, string? NewText);

public interface ITextDiffer
{
    /// <summary>
    ///     Renders a unified diff per entry, keyed by <see cref="TextDiffEntry.Path" />. Entries whose two texts
    ///     are equal are omitted. Must leave no state behind outside its own temp tree.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> DiffAsync(
        IReadOnlyList<TextDiffEntry> entries,
        CancellationToken ct = default);
}

/// <summary>
///     Unified diffs computed by the system <c>git</c> CLI over text handed in by the caller. git is only ever a
///     diff <em>algorithm</em> here: the two sides are written to a temp tree as <c>a/</c> and <c>b/</c> and compared
///     with <c>--no-index</c>, so no revision is ever resolved and no repository is consulted.
/// </summary>
public sealed class GitTextDiffer : ITextDiffer
{
    private const string ChunkHeaderPrefix = "diff --git ";

    /// <summary>0 = trees identical, 1 = differences found. Both are results, not failures.</summary>
    private static readonly int[] DiffSuccessExitCodes = [0, 1];

    public async Task<IReadOnlyDictionary<string, string>> DiffAsync(
        IReadOnlyList<TextDiffEntry> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        // Paths come from upstream tarballs, so a temp name is a hash of the path rather than the path: it
        // cannot carry a separator, a leading dot, or a rooted prefix out of the temp root, stays inside
        // filename limits however deep or wide the real path is, and keeps A.txt distinct from a.txt on a
        // case-insensitive filesystem.
        var entriesByName = new Dictionary<string, TextDiffEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
            entriesByName[TempName(entry.Path)] = entry;

        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            foreach (var (name, entry) in entriesByName)
            {
                // No encoding argument: .NET's default is BOM-less UTF-8, while Encoding.UTF8 would prepend a
                // BOM that then shows up as a spurious first line in added-file diffs.
                if (entry.OldText is { } oldText)
                    await File.WriteAllTextAsync(Path.Combine(root, "a", name), oldText, ct);
                if (entry.NewText is { } newText)
                    await File.WriteAllTextAsync(Path.Combine(root, "b", name), newText, ct);
            }

            // --no-prefix strips git's extra a/+b/ prefix and leaves cwd-relative labels, which is why the
            // process runs with the temp root as its working directory. --no-renames keeps one chunk per side,
            // so a file that moved between revisions is reported as the removal plus the addition it is.
            string[] args =
            [
                "diff", "--no-index", "--no-color", "--text", "--unified=3", "--no-prefix", "--no-renames",
                "--", "a", "b"
            ];
            var stdout = await GitClient.ExecuteAllowingAsync(root, args, DiffSuccessExitCodes, ct);

            return stdout.Length == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : Parse(stdout, entriesByName);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup: a leaked temp dir beats a failed diff.
            }
        }
    }

    private static string TempName(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));

    /// <summary>
    ///     Splits one multi-file <c>git diff</c> into per-file chunks and relabels each one with the caller's real
    ///     path. git's own label is never trusted: it names the temp file.
    /// </summary>
    private static Dictionary<string, string> Parse(
        string stdout,
        Dictionary<string, TextDiffEntry> entriesByName)
    {
        // Split on '\n' rather than StringReader.ReadLine, which would also eat the CR of a CRLF line and hide
        // the ^M that marks a carriage return in the rendered diff.
        var lines = stdout.Split('\n');
        if (lines[^1].Length == 0)
            lines = lines[..^1]; // git output is newline-terminated

        var chunks = new List<List<string>>();
        foreach (var line in lines)
        {
            if (line.StartsWith(ChunkHeaderPrefix, StringComparison.Ordinal))
                chunks.Add([line]);
            else if (chunks.Count > 0)
                chunks[^1].Add(line); // anything before the first header is git noise, not a file
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            if (entriesByName.TryGetValue(TempNameOf(chunk[0]), out var entry))
                result[entry.Path] = Relabel(chunk, entry.Path);
        }

        return result;
    }

    /// <summary>
    ///     The temp name from a <c>diff --git</c> line. For one-sided files git prints both tokens on the
    ///     existing side (<c>diff --git a/gone.txt a/gone.txt</c> for a removal), so the first token's side
    ///     segment is stripped whichever side it carries.
    /// </summary>
    private static string TempNameOf(string header)
    {
        var token = header[ChunkHeaderPrefix.Length..].Split(' ')[0];
        var slash = token.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? string.Empty : token[(slash + 1)..];
    }

    /// <summary>
    ///     Rewrites the <c>---</c>/<c>+++</c> pair with the caller's real path. git already puts
    ///     <c>/dev/null</c> on whichever side is absent, so that marker is kept and only a path is replaced.
    ///     An added or removed <em>empty</em> file has no such pair at all, so a chunk may carry neither line.
    /// </summary>
    private static string Relabel(List<string> chunk, string path)
    {
        // Only the header region is relabeled: a removed line reading "-- x" is body text that also starts
        // with "--- ", so rewriting stops at the first hunk header.
        var hunk = chunk.FindIndex(line => line.StartsWith("@@", StringComparison.Ordinal));
        var headerEnd = hunk < 0 ? chunk.Count : hunk;

        var lines = new List<string>(chunk.Count) { $"diff --git a/{path} b/{path}" };
        for (var i = 1; i < headerEnd; i++)
        {
            var line = chunk[i];
            if (line.StartsWith("--- ", StringComparison.Ordinal))
                lines.Add(line.StartsWith("--- /dev/null", StringComparison.Ordinal) ? line : $"--- a/{path}");
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                lines.Add(line.StartsWith("+++ /dev/null", StringComparison.Ordinal) ? line : $"+++ b/{path}");
            else
                // Passed through as-is: the blob index line and the add/delete mode lines.
                lines.Add(line);
        }

        if (hunk >= 0)
            lines.AddRange(chunk.GetRange(hunk, chunk.Count - hunk)); // hunks, including "\ No newline…"

        return string.Join('\n', lines) + "\n";
    }
}
