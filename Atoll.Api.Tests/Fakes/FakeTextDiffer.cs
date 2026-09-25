using Atoll.Api.Services.Git;

namespace Atoll.Api.Tests.Fakes;

/// <summary>
///     Records what the service handed to the differ and returns a canned chunk per path, so service tests can
///     assert classification and cap behavior without spawning <c>git</c>. <see cref="Throw" /> makes the differ
///     fail, which is how the graceful-degradation path is covered.
/// </summary>
public sealed class FakeTextDiffer : ITextDiffer
{
    private readonly List<TextDiffEntry> _calls = [];

    public IReadOnlyList<TextDiffEntry> Calls => _calls;

    public bool Throw { get; set; }

    public Task<IReadOnlyDictionary<string, string>> DiffAsync(
        IReadOnlyList<TextDiffEntry> entries,
        CancellationToken ct = default)
    {
        _calls.AddRange(entries);

        if (Throw)
            throw new InvalidOperationException("git is not installed");

        IReadOnlyDictionary<string, string> chunks = entries
            .Where(entry => !string.Equals(entry.OldText, entry.NewText, StringComparison.Ordinal))
            .ToDictionary(
                entry => entry.Path,
                entry => $"diff --git a/{entry.Path} b/{entry.Path}\n@@ -1 +1 @@\n-old\n+new\n",
                StringComparer.Ordinal);

        return Task.FromResult(chunks);
    }
}
