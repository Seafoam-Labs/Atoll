using Atoll.Api.Services.Security.Scanning;
using Xunit;

namespace Atoll.Api.Tests.Security.Scanning;

public class ShellCommandPositionsTests
{
    private static bool IsInvoked(string line, string word)
    {
        var positions = ShellSyntax.ComputeQuotePositions(line);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(line);
        var index = normalized.IndexOf(word, StringComparison.Ordinal);
        Assert.True(index >= 0, $"test case is malformed: no '{word}' in '{line}'");

        return ShellCommandPositions.IsInvokedWord(normalized, index, positions, sourceIndices);
    }

    [Theory]
    // argument of another command
    [InlineData("cd sudo", "sudo", false)]
    // argument past the command's options
    [InlineData("install -Dm755 sudo /f", "sudo", false)]
    // word list element
    [InlineData("for f in sudo; do", "sudo", false)]
    // prose word
    [InlineData("avahi needs sudo installed", "sudo", false)]
    // prompt illustration after a bare $
    [InlineData("echo $ sudo", "sudo", false)]
    // redirect target is a file name
    [InlineData("echo x > sudo", "sudo", false)]
    public void IsInvokedWord_ArgumentPositions_ReturnsFalse(string line, string word, bool expected)
    {
        Assert.Equal(expected, IsInvoked(line, word));
    }

    [Theory]
    [InlineData("sudo ls", "sudo", true)]
    // after a list separator
    [InlineData("ls; sudo ls", "sudo", true)]
    // after an AND list
    [InlineData("ls && sudo ls", "sudo", true)]
    // after a pipe
    [InlineData("ls | sudo tee f", "sudo", true)]
    // subshell body
    [InlineData("(sudo ls)", "sudo", true)]
    // case branch body
    [InlineData("3) sudo pacman ;;", "sudo", true)]
    // after control words
    [InlineData("if ! sudo -n true; then", "sudo", true)]
    [InlineData("then do sudo ls", "sudo", true)]
    // command modifier
    [InlineData("time sudo ls", "sudo", true)]
    // chain of privilege tools
    [InlineData("sudo -u yay sudo ls", "sudo", true)]
    // command substitution body
    [InlineData("echo \"$(sudo ls)\"", "sudo", true)]
    public void IsInvokedWord_CommandPositions_ReturnsTrue(string line, string word, bool expected)
    {
        Assert.Equal(expected, IsInvoked(line, word));
    }

    [Theory]
    // assignment prefix runs the command
    [InlineData("FOO=bar sudo ls", "sudo", true)]
    // through options and assignments
    [InlineData("env -i FOO=bar sudo ls", "sudo", true)]
    // past the option value of a modifier
    [InlineData("nice -n 10 sudo ls", "sudo", true)]
    // xargs runs what it is handed
    [InlineData("xargs curl -s url", "curl", true)]
    // python executes the -m module
    [InlineData("python -m pip install x", "pip", true)]
    // makepkg does not run its arguments
    [InlineData("makepkg sudo ls", "sudo", false)]
    public void IsInvokedWord_NonGoverningTokens_WalksBackToCommandPosition(string line, string word, bool expected)
    {
        Assert.Equal(expected, IsInvoked(line, word));
    }

    [Fact]
    public void IsInvokedWord_AfterClosedCommandSubstitution_StaysInCommandPosition()
    {
        // A word after an unquoted $( … ) is normally its consumer's argument, which the
        // consumer list decides. Command position is kept here because the alternative drops
        // the genuine 'VAR=$(…) sudo cmd' assignment prefix, where the shell does run cmd.
        Assert.True(IsInvoked("cmd $(y) sudo x", "sudo"));
    }
}
