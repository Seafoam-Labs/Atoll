namespace Atoll.Api.Services.Security.Scanning;

/// <summary>
///     Decides whether the word at a position of a de-obfuscated line is the command a shell
///     would run, or an argument/assignment data handed to another command. Being a shell word
///     is not enough: <c>sudo cmd</c> invokes sudo, <c>cd sudo</c> names a directory.
///     <c>eval-indirection</c> keeps its own narrower predicate in <see cref="ShellEvalClassifier" />:
///     beyond command position it requires a dynamic operand, and its corpus-tuned severity split
///     is pinned independently.
/// </summary>
internal static class ShellCommandPositions
{
    // Words after which the next word is itself a command: control-flow keywords, the modifiers
    // that prefix a command, and the privilege tools, which run what follows them
    // ('sudo -u x pkexec y' chains, 'if sudo -n true'). 'for', 'in', 'case' and 'select' are
    // deliberately absent - their operand is a variable name or a word list, not a command.
    private static readonly HashSet<string> CommandPositionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "then", "else", "elif", "fi", "do", "done", "esac", "while", "until", "and", "or", "!",
        "time", "exec", "command", "eval", "source", ".", "nohup", "nice", "env", "chrt", "taskset",
        "setsid", "coproc",
        "sudo", "sudoedit", "doas", "pkexec", "run0", "su"
    };

    // Commands that are not prefix-like yet still run the words handed to them, so a tool in
    // their argument position executes: 'python -m pip install x', 'xargs curl …', 'ssh host …'.
    private static readonly HashSet<string> ArgumentExecutingCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "xargs", "parallel", "watch", "script", "systemd-run", "runuser", "unshare", "nsenter", "bwrap",
        "fakeroot", "ssh",
        "sh", "bash", "zsh", "dash", "ksh", "fish", "expect", "tclsh",
        "python", "python2", "python3", "pypy", "node", "deno", "bun",
        "ruby", "perl", "php", "lua", "luajit", "Rscript", "julia", "wine"
    };

    /// <summary>
    ///     True when the word starting at <paramref name="index" /> sits in command position: at
    ///     line start, after a command separator or control word, after an assignment prefix
    ///     (<c>FOO=bar sudo x</c> runs sudo), past the option list of a command that executes
    ///     its arguments (<c>install -Dm755 sudo …</c> does not), or inside a command
    ///     substitution. Only words that survive shell quote removal are considered, so the
    ///     caller's quoted-display-text exemptions still apply first.
    /// </summary>
    internal static bool IsInvokedWord(
        string normalized,
        int index,
        ShellSyntax.QuotePosition[] positions,
        int[] sourceIndices)
    {
        while (true)
        {
            var end = index;
            while (end > 0 && char.IsWhiteSpace(normalized[end - 1]))
                end--;

            if (end == 0)
                return true;

            if (IsCommandSeparator(normalized, end - 1, positions, sourceIndices))
                return true;

            var start = end;
            while (start > 0 && !IsWordEnd(normalized[start - 1]))
                start--;

            // An empty token means a redirect operator sits directly before the word, which
            // makes it the target of the redirect: 'echo x > sudo' writes a file named sudo.
            if (start == end)
                return false;

            var token = normalized[start..end];
            if (IsOptionToken(token) || IsAssignmentToken(token))
            {
                index = start;
                continue;
            }

            return CommandPositionWords.Contains(token) || ArgumentExecutingCommands.Contains(token);
        }
    }

    private static bool IsCommandSeparator(
        string normalized,
        int index,
        ShellSyntax.QuotePosition[] positions,
        int[] sourceIndices)
    {
        if (normalized[index] is not (';' or '&' or '|' or '(' or ')' or '`' or '{' or '}'))
            return false;

        var position = positions[sourceIndices[index]];

        // A quoted or escaped operator is data: only the shell's own separators chain commands.
        return position is { Escaped: false, Region: ShellSyntax.QuoteRegion.Normal or ShellSyntax.QuoteRegion.CommandSubstitution };
    }

    private static bool IsWordEnd(char c)
    {
        return char.IsWhiteSpace(c) || c is ';' or '&' or '|' or '(' or ')' or '`' or '{' or '}' or '<' or '>';
    }

    // An option ('-Dm755', '--prefix=/usr') or a bare numeric option value ('nice -n 10 sudo x')
    // belongs to the governing command, so the walk keeps going back to find it.
    private static bool IsOptionToken(string token)
    {
        return token[0] == '-' || token.All(char.IsAsciiDigit);
    }

    // 'FOO=bar sudo x' runs sudo with FOO set: the assignment prefixes the command, it is not
    // the command.
    private static bool IsAssignmentToken(string token)
    {
        var separator = token.IndexOf('=');
        if (separator <= 0)
            return false;

        for (var i = 0; i < separator; i++)
            if (!ShellSyntax.IsWordChar(token[i]))
                return false;

        return true;
    }
}