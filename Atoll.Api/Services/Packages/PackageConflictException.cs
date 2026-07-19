namespace Atoll.Api.Services.Packages;

public sealed class PackageConflictException(string packageName) : Exception
{
    public string PackageName { get; } = packageName;

    public override string Message => $"Package '{PackageName}' already exists.";
}