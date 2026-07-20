using System.Text;
using CliWrap;
using CliWrap.Exceptions;

namespace Atoll.Api.Services.Packages.Git;

public sealed class GitTransferService(IPackageService packages) : IGitTransferService
{
    public async Task<GitTransferResult> AdvertiseRefsAsync(string name, Stream output, CancellationToken ct)
    {
        var gitDir = await ResolveRepositoryAsync(name, ct);
        if (gitDir is null)
            return new GitTransferResult.NotFound();

        await WritePacketLineAsync(output, "# service=git-upload-pack\n", ct);
        await WriteFlushAsync(output, ct);
        await output.FlushAsync(ct);

        string[] arguments = ["upload-pack", "--stateless-rpc", "--advertise-refs", gitDir];
        var error = new StringBuilder();

        var cmd = Cli.Wrap("git")
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(error))
            .WithStandardOutputPipe(PipeTarget.ToStream(output));

        try
        {
            await cmd.ExecuteAsync(ct);
        }
        catch (CommandExecutionException ex)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {error.ToString().Trim()}",
                ex);
        }

        return new GitTransferResult.Ok();
    }

    public async Task<GitTransferResult> UploadPackAsync(string name, Stream input, Stream output, CancellationToken ct)
    {
        var gitDir = await ResolveRepositoryAsync(name, ct);
        if (gitDir is null)
            return new GitTransferResult.NotFound();

        string[] arguments = ["upload-pack", "--stateless-rpc", gitDir];
        var error = new StringBuilder();

        var cmd = Cli.Wrap("git")
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .WithStandardInputPipe(PipeSource.FromStream(input))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(error))
            .WithStandardOutputPipe(PipeTarget.ToStream(output));

        try
        {
            await cmd.ExecuteAsync(ct);
        }
        catch (CommandExecutionException ex)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {error.ToString().Trim()}",
                ex);
        }

        return new GitTransferResult.Ok();
    }

    private async Task<string?> ResolveRepositoryAsync(string name, CancellationToken ct)
    {
        if (!await packages.ExistsAsync(name, ct))
            return null;

        await packages.EnsureGitRepositoryAsync(name, ct);

        var gitDir = packages.GetRepositoryPath(name);
        if (gitDir is null || !Directory.Exists(gitDir))
            return null;

        return gitDir;
    }

    private static async Task WritePacketLineAsync(Stream output, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var length = (bytes.Length + 4).ToString("x4");
        await output.WriteAsync(Encoding.UTF8.GetBytes(length).AsMemory(0, 4), ct);
        await output.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);
    }

    private static async Task WriteFlushAsync(Stream output, CancellationToken ct)
    {
        await output.WriteAsync("0000"u8.ToArray().AsMemory(0, 4), ct);
    }
}