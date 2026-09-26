using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Tests.Fakes;

namespace Atoll.Api.Tests.Catalog.Persistence;

public sealed class AurMetadataRepositoryFakeTests : AurMetadataRepositoryContract
{
    private protected override IAurMetadataRepository CreateRepository()
    {
        return new InMemoryAurMetadataRepository();
    }
}
