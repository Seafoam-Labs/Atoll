using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;

namespace Atoll.Api.Tests.Security;

public class PackageSecurityRepositoryTests : PackageSecurityRepositoryContract
{
    private protected override IPackageSecurityRepository CreateRepository()
    {
        return new InMemoryPackageSecurityRepository();
    }
}
