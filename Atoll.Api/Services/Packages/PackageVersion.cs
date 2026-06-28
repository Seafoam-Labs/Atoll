namespace Atoll.Api.Services.Packages;

public record PackageVersion(
    string Sha,
    DateTimeOffset Date,
    string Message,
    string Author);