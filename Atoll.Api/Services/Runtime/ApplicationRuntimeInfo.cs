namespace Atoll.Api.Services.Runtime;

public sealed class ApplicationRuntimeInfo(DateTimeOffset startedAtUtc)
{
    public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
}