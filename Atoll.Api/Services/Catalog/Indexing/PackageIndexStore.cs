namespace Atoll.Api.Services.Catalog.Indexing;

public sealed class PackageIndexStore
{
    private SearchIndexData _current = SearchIndexData.Empty;

    public SearchIndexData Current => Volatile.Read(ref _current);

    public void Replace(SearchIndexData next)
    {
        Volatile.Write(ref _current, next);
    }
}