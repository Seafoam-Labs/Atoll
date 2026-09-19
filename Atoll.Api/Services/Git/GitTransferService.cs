using System.Text;
using CliWrap;
using CliWrap.Exceptions;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Catalog.Rpc;

namespace Atoll.Api.Services.Git;

public sealed class GitTransferService(
    IPackageRepository packages,
    IGitRepositoryCache repositoryCache,
    AurRpcService rpc)
    : IGitTransferService
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

        // Buffered so wants can be validated before upload-pack runs: its own rejection for an
        // unknown ref arrives after the response has started, which surfaces as a mid-stream
        // exception instead of a protocol error.
        using var body = new MemoryStream();
        await input.CopyToAsync(body, ct);

        var wants = ReadWants(body.GetBuffer().AsSpan(0, (int)body.Length));
        if (wants is { Count: > 0 })
        {
            var advertised = await GetAdvertisedObjectIdsAsync(gitDir, ct);
            if (advertised is not null)
            {
                var unknown = wants.FirstOrDefault(want => !advertised.Contains(want));
                if (unknown is not null)
                {
                    await WritePacketLineAsync(output, $"ERR upload-pack: not our ref {unknown.ToLowerInvariant()}", ct);
                    await output.FlushAsync(ct);
                    return new GitTransferResult.Ok();
                }
            }
        }

        body.Position = 0;

        string[] arguments = ["upload-pack", "--stateless-rpc", gitDir];
        var error = new StringBuilder();

        var cmd = Cli.Wrap("git")
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .WithStandardInputPipe(PipeSource.FromStream(body))
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
        foreach (var candidate in rpc.ResolvePackageNames(name))
        {
            if (!await packages.ExistsAsync(candidate, ct))
                continue;

            await repositoryCache.EnsureRepositoryAsync(candidate, ct);

            var gitDir = repositoryCache.GetRepositoryPath(candidate);
            if (gitDir is not null && Directory.Exists(gitDir))
                return gitDir;
        }

        return null;
    }

    /// <summary>
    ///     Wants from a v0 upload-pack request body. The first flush ends the want list; lines after
    ///     it (<c>have</c>, <c>done</c>) are not wants. Returns <c>null</c> when the pkt-line framing
    ///     cannot be walked, which skips validation and forwards the body unchanged.
    /// </summary>
    private static List<string>? ReadWants(ReadOnlySpan<byte> body)
    {
        var wants = new List<string>();
        var offset = 0;

        while (offset < body.Length)
        {
            if (offset + 4 > body.Length || !TryParseHexLength(body.Slice(offset, 4), out var length))
                return null;

            if (length == 0)
                return wants;

            if (length < 4 || offset + length > body.Length)
                return null;

            var line = body.Slice(offset + 4, length - 4);
            offset += length;

            if (!line.StartsWith("want "u8))
                continue;

            var rest = line[5..];
            var end = rest.IndexOf((byte)' ');
            if (end < 0)
                end = rest.IndexOf((byte)'\n');
            if (end < 0)
                end = rest.Length;

            wants.Add(Encoding.ASCII.GetString(rest[..end]));
        }

        return null;
    }

    private static bool TryParseHexLength(ReadOnlySpan<byte> prefix, out int length)
    {
        length = 0;
        foreach (var b in prefix)
        {
            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                _ => -1
            };

            if (digit < 0)
                return false;

            length = (length << 4) | digit;
        }

        return true;
    }

    /// <summary>
    ///     Object ids in the repository's advertisement, or <c>null</c> when the query failed in a
    ///     way that leaves the advertised set unknown. Peeled tag targets are their own
    ///     <c>^{}</c> lines, so the first token of every line covers them.
    /// </summary>
    private static async Task<HashSet<string>?> GetAdvertisedObjectIdsAsync(string gitDir, CancellationToken ct)
    {
        var (exitCode, output) = await GitClient.TryExecuteAsync(["-C", gitDir, "show-ref", "--head", "-d"], ct);
        if (exitCode > 1)
            return null;

        var advertised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length >= 40 && line[..40].All(Uri.IsHexDigit))
                advertised.Add(line[..40]);
        }

        return advertised;
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