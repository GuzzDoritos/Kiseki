using Kiseki.Core.DTOs;
using Kiseki.Web.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Kiseki.Web.Services;

public interface ITtsuImportBatchStore
{
    TtsuImportBatch Store(IReadOnlyList<TtsuBookContainer> books);
    bool TryGet(Guid batchId, out TtsuImportBatch batch);
    void Remove(Guid batchId);
}

public sealed class TtsuImportBatchStore(IMemoryCache cache) : ITtsuImportBatchStore
{
    private static readonly TimeSpan BatchLifetime = TimeSpan.FromMinutes(30);

    public TtsuImportBatch Store(IReadOnlyList<TtsuBookContainer> books)
    {
        ArgumentNullException.ThrowIfNull(books);

        var batch = new TtsuImportBatch(
            Guid.NewGuid(),
            books.Select(book => new TtsuImportBatchBook(Guid.NewGuid(), book)).ToList());

        cache.Set(
            CacheKey(batch.Id),
            batch,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = BatchLifetime
            });

        return batch;
    }

    public bool TryGet(Guid batchId, out TtsuImportBatch batch)
    {
        if (cache.TryGetValue(CacheKey(batchId), out TtsuImportBatch? storedBatch) &&
            storedBatch is not null)
        {
            batch = storedBatch;
            return true;
        }

        batch = null!;
        return false;
    }

    public void Remove(Guid batchId)
    {
        cache.Remove(CacheKey(batchId));
    }

    private static string CacheKey(Guid batchId) => $"ttsu-import:{batchId:N}";
}
