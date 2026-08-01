using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Tests.Fakes;

namespace Atoll.Api.Tests.Search.Indexing;

public class AurMetadataRepositoryTests : AurMetadataRepositoryContract
{
    private protected override IAurMetadataRepository CreateRepository()
    {
        return new InMemoryAurMetadataRepository();
    }
}