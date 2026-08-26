using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Tests.Fakes;

namespace Atoll.Api.Tests.Catalog.Indexing;

public class AurMetadataRepositoryTests : AurMetadataRepositoryContract
{
    private protected override IAurMetadataRepository CreateRepository()
    {
        return new InMemoryAurMetadataRepository();
    }
}