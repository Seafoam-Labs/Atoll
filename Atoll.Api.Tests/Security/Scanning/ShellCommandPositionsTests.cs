using Atoll.Api.Services.Security.Scanning;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security.Scanning;

public class ShellCommandPositionsTests
{
    private static bool IsInvoked(string line, string word)
    {
        var positions = ShellSyntax.ComputeQuotePositions(line);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(line);
        var index = normalized.IndexOf(word, StringComparison.Ordinal);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), $"test case is malformed: no '{word}' in '{line}'");

        return ShellCommandPositions.IsInvokedWord(normalized, index, positions, sourceIndices);
    }

    [TestCase("cd sudo", "sudo", false, Description = "argument of another command")]
    [TestCase("install -Dm755 sudo /f", "sudo", false, Description = "argument past the command's options")]
    [TestCase("for f in sudo; do", "sudo", false, Description = "word list element")]
    [TestCase("avahi needs sudo installed", "sudo", false, Description = "prose word")]
    [TestCase("echo $ sudo", "sudo", false, Description = "prompt illustration after a bare $")]
    [TestCase("echo x > sudo", "sudo", false, Description = "redirect target is a file name")]
    public void Reports_argument_position(string line, string word, bool expected)
    {
        Assert.That(IsInvoked(line, word), Is.EqualTo(expected));
    }

    [TestCase("sudo ls", "sudo", true)]
    [TestCase("ls; sudo ls", "sudo", true, Description = "after a list separator")]
    [TestCase("ls && sudo ls", "sudo", true, Description = "after an AND list")]
    [TestCase("ls | sudo tee f", "sudo", true, Description = "after a pipe")]
    [TestCase("(sudo ls)", "sudo", true, Description = "subshell body")]
    [TestCase("3) sudo pacman ;;", "sudo", true, Description = "case branch body")]
    [TestCase("if ! sudo -n true; then", "sudo", true, Description = "after control words")]
    [TestCase("then do sudo ls", "sudo", true)]
    [TestCase("time sudo ls", "sudo", true, Description = "command modifier")]
    [TestCase("sudo -u yay sudo ls", "sudo", true, Description = "chain of privilege tools")]
    [TestCase("echo \"$(sudo ls)\"", "sudo", true, Description = "command substitution body")]
    public void Reports_command_position(string line, string word, bool expected)
    {
        Assert.That(IsInvoked(line, word), Is.EqualTo(expected));
    }

    [TestCase("FOO=bar sudo ls", "sudo", true, Description = "assignment prefix runs the command")]
    [TestCase("env -i FOO=bar sudo ls", "sudo", true, Description = "through options and assignments")]
    [TestCase("nice -n 10 sudo ls", "sudo", true, Description = "past the option value of a modifier")]
    [TestCase("xargs curl -s url", "curl", true, Description = "xargs runs what it is handed")]
    [TestCase("python -m pip install x", "pip", true, Description = "python executes the -m module")]
    [TestCase("makepkg sudo ls", "sudo", false, Description = "makepkg does not run its arguments")]
    public void Walks_back_over_tokens_that_do_not_govern_the_word(string line, string word, bool expected)
    {
        Assert.That(IsInvoked(line, word), Is.EqualTo(expected));
    }

    [Test]
    public void Closing_a_command_substitution_stays_in_command_position()
    {
        // A word after an unquoted $( … ) is normally its consumer's argument, which the
        // consumer list decides. Command position is kept here because the alternative drops
        // the genuine 'VAR=$(…) sudo cmd' assignment prefix, where the shell does run cmd.
        Assert.That(IsInvoked("cmd $(y) sudo x", "sudo"), Is.True);
    }
}
