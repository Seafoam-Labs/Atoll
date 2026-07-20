using System.Text;
using CliWrap;
using CliWrap.Exceptions;
using static CliWrap.CommandResultValidation;

namespace Atoll.Api.Services.Packages.Git;

public static class GitClient
{
    public static async Task<string> ExecuteAsync(
        string workingDirectory,
        string[] arguments,
        string? input,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken cancellationToken = default)
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
            cmd = cmd.WithStandardInputPipe(PipeSource.FromString(input));

        if (env is not null)
        {
            cmd = cmd.WithEnvironmentVariables(b =>
            {
                foreach (var (key, value) in env)
                    b.Set(key, value);
            });
        }

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

    public static async Task CloneAsync(
        string sourceUrl,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        string[] arguments = ["clone", sourceUrl, targetPath];
        await ExecuteAsync(Directory.GetCurrentDirectory(), arguments, input: null, env: null, cancellationToken);
    }

    public static async Task<(int ExitCode, string Output)> TryExecuteAsync(
        string[] arguments,
        CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();

        var result = await Cli.Wrap("git")
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
            .ExecuteAsync(cancellationToken);

        return (result.ExitCode, output.ToString());
    }
}