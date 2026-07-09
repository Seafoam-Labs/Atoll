using System.Text;
using CliWrap;
using CliWrap.Exceptions;
using static CliWrap.CommandResultValidation;

namespace Atoll.Api.Services.Packages;

/// <summary>
///     Implements GIT Smart HTTP Proxy. https://git-scm.com/docs/http-protocol
/// </summary>
/// <param name="packages"></param>
public sealed class GitTransferService(IPackageService packages) : IGitTransferService
{
    public async Task<GitTransferResult> AdvertiseRefsAsync(string name, Stream output, CancellationToken ct)
    {
        var gitDir = packages.GetRepositoryPath(name);
        if (gitDir is null || !Directory.Exists(gitDir))
            return new GitTransferResult.NotFound();

        await WritePacketLineAsync(output, "# service=git-upload-pack\n", ct);
        await WriteFlushAsync(output, ct);

        await RunUploadPackAsync(
            ["upload-pack", "--stateless-rpc", "--advertise-refs", gitDir],
            null,
            output,
            ct);

        return new GitTransferResult.Ok();
    }

    public async Task<GitTransferResult> UploadPackAsync(string name, Stream input, Stream output, CancellationToken ct)
    {
        var gitDir = packages.GetRepositoryPath(name);
        if (gitDir is null || !Directory.Exists(gitDir))
            return new GitTransferResult.NotFound();

        await RunUploadPackAsync(
            ["upload-pack", "--stateless-rpc", gitDir],
            input,
            output,
            ct);

        return new GitTransferResult.Ok();
    }

    private static async Task RunUploadPackAsync(string[] args, Stream? input, Stream output, CancellationToken ct)
    {
        var error = new StringBuilder();

        var cmd = Cli.Wrap("git")
            .WithArguments(args)
            .WithValidation(ZeroExitCode)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(error));

        if (input is not null)
            cmd = cmd.WithStandardInputPipe(PipeSource.FromStream(input));

        cmd = cmd.WithStandardOutputPipe(PipeTarget.ToStream(output));

        try
        {
            await cmd.ExecuteAsync(ct);
        }
        catch (CommandExecutionException)
        {
            throw new InvalidOperationException(error.ToString().Trim());
        }
    }

    private static async Task WritePacketLineAsync(Stream output, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var length = (bytes.Length + 4).ToString("X4");
        await output.WriteAsync(Encoding.UTF8.GetBytes(length), ct);
        await output.WriteAsync(bytes, ct);
    }

    private static async Task WriteFlushAsync(Stream output, CancellationToken ct)
    {
        await output.WriteAsync("0000"u8.ToArray(), ct);
    }
}