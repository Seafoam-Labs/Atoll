namespace Atoll.Api.Services.Indexing;

public sealed class PackageIndexStore
{
    private PackageIndexes _current = PackageIndexes.Empty;

    public PackageIndexes Current => Volatile.Read(ref _current);

    public void Replace(PackageIndexes next)
    {
        Volatile.Write(ref _current, next);
    }
}