using System.Text;
using CliWrap;
using CliWrap.Exceptions;
using static CliWrap.CommandResultValidation;

namespace Atoll.Api.Services.Git;

public static class GitClient
{
    public static Task<string> ExecuteAsync(
        string workingDirectory,
        string[] arguments,
        string? input,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCoreAsync(
            workingDirectory,
            arguments,
            input is null ? null : PipeSource.FromString(input),
            env,
            cancellationToken);
    }

    /// <summary>
    ///     Runs git with a byte-exact stdin. Use this instead of <see cref="ExecuteAsync" /> when the input
    ///     is file content: decoding to a string and re-encoding would replace invalid UTF-8.
    /// </summary>
    public static Task<string> ExecuteWithInputAsync(
        string workingDirectory,
        string[] arguments,
        byte[] input,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCoreAsync(
            workingDirectory,
            arguments,
            PipeSource.FromBytes(input),
            env,
            cancellationToken);
    }

    private static async Task<string> ExecuteCoreAsync(
        string workingDirectory,
        string[] arguments,
        PipeSource? input,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();

        var cmd = Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(arguments)
            .WithValidation(ZeroExitCode)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(error));

        if (input is not null)
            cmd = cmd.WithStandardInputPipe(input);

        if (env is not null)
            cmd = cmd.WithEnvironmentVariables(b =>
            {
                foreach (var (key, value) in env)
                    b.Set(key, value);
            });

        try
        {
            await cmd.ExecuteAsync(cancellationToken);
            return output.ToString();
        }
        catch (CommandExecutionException ex)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.ToString().Trim()}", ex);
        }
    }

    /// <summary>
    ///     Runs git and returns stdout, tolerating the exit codes in <paramref name="successExitCodes" />.
    ///     <c>git diff --no-index</c> exits 1 to mean "differences found", which is a result, not a failure,
    ///     so <see cref="ExecuteAsync" />'s zero-exit-code validation would throw on the success path.
    /// </summary>
    public static async Task<string> ExecuteAllowingAsync(
        string workingDirectory,
        string[] arguments,
        IReadOnlyCollection<int> successExitCodes,
        CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();

        using var commandTask = Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(arguments)
            .WithValidation(None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(error))
            .ExecuteAsync(cancellationToken);
        var result = await commandTask.Task;

        if (!successExitCodes.Contains(result.ExitCode))
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {error.ToString().Trim()}");

        return output.ToString();
    }

    public static async Task CloneAsync(
        string sourceUrl,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        string[] arguments = ["clone", sourceUrl, targetPath];
        await ExecuteAsync(Directory.GetCurrentDirectory(), arguments, null, null, cancellationToken);
    }

    public static async Task<(int ExitCode, string Output)> TryExecuteAsync(
        string[] arguments,
        CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();

        using var commandTask = Cli.Wrap("git")
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
            .ExecuteAsync(cancellationToken);
        var result = await commandTask.Task;

        return (result.ExitCode, output.ToString());
    }
}