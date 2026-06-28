namespace Atoll.Api.Services.Search.Indexing;

public sealed class PackageIndexStore
{
    private SearchIndexData _current = SearchIndexData.Empty;

    public SearchIndexData Current => Volatile.Read(ref _current);

    public void Replace(SearchIndexData next)
    {
        Volatile.Write(ref _current, next);
    }
}